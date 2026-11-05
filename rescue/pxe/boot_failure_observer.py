"""Strict visual evidence for the bootstrap diagnostic, never a LAB approval.

Tesseract TSV word confidences are averaged across the complete expected line;
the required mean is >= 80. No fuzzy text, substrings or command traces count.
Two independently captured frames at least 0.3 seconds apart are required.
Only the observed OCR ambiguity between semicolon and colon immediately after
"rede/servidor" is accepted, as two exact complete lines with audited raw text.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import csv
from datetime import datetime, timezone
from decimal import Decimal
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time

EXPECTED_FAILURE = "Falha de boot. Verifique rede/servidor; nenhum disco foi modificado."
OCR_COLON_FAILURE = "Falha de boot. Verifique rede/servidor: nenhum disco foi modificado."
ACCEPTED_OCR_LINES = (EXPECTED_FAILURE, OCR_COLON_FAILURE)
MINIMUM_MEAN_CONFIDENCE = 80.0
MINIMUM_FRAME_SEPARATION = 0.3
CAPTURE_TIMES = (0.3, 1.0, 3.0, 5.0, 7.0, 9.0, 11.0, 15.0, 30.0, 60.0, 120.0, 170.0)


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def failure_line_from_tsv(tsv):
    """Require the exact complete line and all its words, including any prefix."""
    reader = csv.DictReader(io.StringIO(tsv), delimiter="\t")
    required = {"level", "page_num", "block_num", "par_num", "line_num", "word_num", "left", "top", "width", "height", "conf", "text"}
    if reader.fieldnames is None or not required.issubset(reader.fieldnames):
        return None
    lines = {}
    all_words = []
    for row in reader:
        if row.get("level") != "5" or not row.get("text", "").strip():
            continue
        try:
            key = tuple(int(row[k]) for k in ("page_num", "block_num", "par_num", "line_num"))
            word = {"text": row["text"].strip(), "confidence": float(row["conf"]),
                    **{k: int(row[k]) for k in ("word_num", "left", "top", "width", "height")}}
        except (TypeError, ValueError):
            # A malformed word must not be silently removed from a candidate.
            return None
        lines.setdefault(key, []).append(word)
        all_words.append(word)
    for key, words in lines.items():
        words.sort(key=lambda word: (word["word_num"], word["left"]))
        text = " ".join(word["text"] for word in words)
        if text not in ACCEPTED_OCR_LINES or any(not 0 <= word["confidence"] <= 100 for word in words):
            continue
        confidence = sum(word["confidence"] for word in words) / len(words)
        if confidence < MINIMUM_MEAN_CONFIDENCE:
            continue
        # OCR can split a trace prefix into another block despite the same
        # physical baseline. Include every recognized word on that row, so
        # a separate "echo" block cannot turn a command trace into evidence.
        row_words = [word for word in all_words if any(
            min(word["top"] + word["height"], candidate["top"] + candidate["height"]) - max(word["top"], candidate["top"])
            >= min(word["height"], candidate["height"]) / 2 for candidate in words)]
        row_words.sort(key=lambda word: word["left"])
        if " ".join(word["text"] for word in row_words) != text:
            continue
        left = min(word["left"] for word in words)
        top = min(word["top"] for word in words)
        return {"text": EXPECTED_FAILURE, "ocrText": text, "normalizedSeparator": text == OCR_COLON_FAILURE,
                "rowAnchored": True, "meanConfidence": round(confidence, 6), "wordConfidences": [word["confidence"] for word in words],
                "lineKey": list(key), "box": {"left": left, "top": top,
                    "width": max(word["left"] + word["width"] for word in words) - left,
                    "height": max(word["top"] + word["height"] for word in words) - top}}
    return None


def corroborating_frames(frames):
    candidates = [frame for frame in frames if frame.get("candidate") and frame["candidate"].get("rowAnchored") is True
                  and frame["candidate"].get("text") == EXPECTED_FAILURE
                  and frame["candidate"].get("ocrText") in ACCEPTED_OCR_LINES
                  and frame["candidate"].get("meanConfidence", -1) >= MINIMUM_MEAN_CONFIDENCE]
    candidates.sort(key=lambda frame: frame["capturedSeconds"])
    for first_index, first in enumerate(candidates):
        for second in candidates[first_index + 1:]:
            separation = Decimal(str(second["capturedSeconds"])) - Decimal(str(first["capturedSeconds"]))
            if first["ppm"] != second["ppm"] and separation >= Decimal(str(MINIMUM_FRAME_SEPARATION)):
                return [first["ppm"], second["ppm"]]
    return None


def _ocr_input(ppm, destination):
    """Lossless P6 reader; isolate neutral light glyphs and scale 3x for OCR."""
    data = Path(ppm).read_bytes()
    if len(data) > 32 * 1024 * 1024:
        raise ValueError("QEMU frame exceeds bounded input size")
    position = 0
    tokens = []
    for _ in range(4):
        while position < len(data):
            if data[position:position + 1] == b"#":
                position = data.index(b"\n", position) + 1
            elif data[position] in b" \t\r\n":
                position += 1
            else:
                break
        start = position
        while position < len(data) and data[position] not in b" \t\r\n":
            position += 1
        tokens.append(data[start:position])
    if tokens[0] != b"P6" or tokens[3] != b"255":
        raise ValueError("Expected QEMU RGB P6 frame with 8-bit components")
    width, height = int(tokens[1]), int(tokens[2])
    if not (1 <= width <= 4096 and 1 <= height <= 2160):
        raise ValueError("Unexpected QEMU frame dimensions")
    position += 2 if data[position:position + 2] == b"\r\n" else 1
    pixels = data[position:]
    if len(pixels) != width * height * 3:
        raise ValueError("Truncated QEMU frame")
    with Path(destination).open("wb") as output:
        output.write(f"P5\n{width * 3} {height * 3}\n255\n".encode("ascii"))
        for row in range(height):
            scaled = bytearray()
            for column in range(width):
                offset = (row * width + column) * 3
                rgb = pixels[offset:offset + 3]
                value = 0 if min(rgb) >= 80 and max(rgb) - min(rgb) <= 70 else 255
                scaled.extend(bytes((value,)) * 3)
            output.write(scaled * 3)


def recognize_frame(ppm, tesseract):
    ppm = Path(ppm)
    tsv_path, text_path = ppm.with_suffix(".tsv"), ppm.with_suffix(".txt")
    result = {"ppm": ppm.name, "ppmSha256": sha256(ppm), "source": "QEMU_VGA_TESSERACT_TSV", "candidate": None}
    prepared = None
    try:
        with tempfile.NamedTemporaryFile(prefix="slt-ocr-", suffix=".pgm", dir=ppm.parent, delete=False) as temporary:
            prepared = Path(temporary.name)
        _ocr_input(ppm, prepared)
        process = subprocess.run([tesseract, str(prepared), "stdout", "-l", "eng", "--psm", "6", "tsv"],
                                 capture_output=True, text=True, encoding="utf-8", timeout=15)
        if process.returncode != 0:
            raise RuntimeError("Tesseract failed: " + process.stderr[-300:])
        tsv = process.stdout
        result["candidate"] = failure_line_from_tsv(tsv)
    except Exception as error:
        tsv = ""
        result["ocrError"] = type(error).__name__ + ": " + str(error)[:300]
    finally:
        if prepared:
            # This is solely the generated temporary OCR input in this frame's directory.
            assert prepared.resolve().parent == ppm.resolve().parent
            prepared.unlink(missing_ok=True)
    tsv_path.write_text(tsv, encoding="utf-8")
    words = csv.DictReader(io.StringIO(tsv), delimiter="\t") if tsv else []
    lines = {}
    for word in words:
        if word.get("level") == "5" and word.get("text", "").strip():
            key = tuple(word.get(k) for k in ("page_num", "block_num", "par_num", "line_num"))
            lines.setdefault(key, []).append(word["text"].strip())
    text_path.write_text("\n".join(" ".join(line) for line in lines.values()) + "\n", encoding="utf-8")
    result.update(tsv=tsv_path.name, tsvSha256=sha256(tsv_path), text=text_path.name, textSha256=sha256(text_path))
    return result


def observe_failure(console, qmp, output, prefix, normalize_serial, *, selected_at, visual_only=False, timeout=180):
    """Collect bounded frames while pumping serial; no reset/firmware screen suffices."""
    output = Path(output)
    tesseract = shutil.which("tesseract")
    if not tesseract:
        raise FileNotFoundError("--visual-evidence requires the official Tesseract CLI")
    report = {"expectedFailure": EXPECTED_FAILURE, "minimumMeanConfidence": MINIMUM_MEAN_CONFIDENCE,
              "acceptedOcrLines": list(ACCEPTED_OCR_LINES),
              "minimumFrameSeparationSeconds": MINIMUM_FRAME_SEPARATION, "visualOnlyDiagnostic": visual_only,
              "frames": [], "serialExplicitFailureObserved": False, "visualCorroboration": None, "passed": False}
    report_path = output / (prefix + "-visual-observation.json")
    futures, captured = [], []
    pool = ThreadPoolExecutor(max_workers=2)
    capture_index = 0
    serial_pattern = re.compile(r"(?m)^" + re.escape(EXPECTED_FAILURE) + r"$")
    try:
        while time.monotonic() - selected_at < timeout:
            text = normalize_serial(console.pump())
            if re.search(r"SLT_RESCUE_BOOT_READY:|Linux version |BOOT_READY", text):
                raise AssertionError("Unexpected Linux output during bootstrap failure observation")
            report["serialExplicitFailureObserved"] |= bool(serial_pattern.search(text))
            for index, future in list(futures):
                if future.done():
                    frame = captured[index]
                    frame.update(future.result())
                    report["frames"].append(frame)
                    futures.remove((index, future))
            report["visualCorroboration"] = corroborating_frames(report["frames"])
            proof = report["visualCorroboration"] or (report["serialExplicitFailureObserved"] and not visual_only)
            # Always capture 0.3/1/3 second counterparts, even when serial works.
            if proof and capture_index >= 3 and not futures:
                report["passed"] = True
                report["source"] = "VGA_EXPLICIT_FAILURE_TWO_FRAMES" if report["visualCorroboration"] else "SERIAL_EXPLICIT_FAILURE"
                return report
            elapsed = time.monotonic() - selected_at
            if not (proof and capture_index >= 3) and capture_index < len(CAPTURE_TIMES) and elapsed >= CAPTURE_TIMES[capture_index]:
                ppm = output / (prefix + ("-after-selection.ppm" if capture_index == 0 else f"-visual-{capture_index + 1:02d}.ppm"))
                captured_at = time.monotonic() - selected_at
                qmp.execute("screendump", {"filename": str(ppm)})
                captured.append({"capturedSeconds": round(captured_at, 6), "scheduledSeconds": CAPTURE_TIMES[capture_index],
                                 "capturedAtUtc": datetime.now(timezone.utc).isoformat(), "ppm": ppm.name})
                futures.append((capture_index, pool.submit(recognize_frame, ppm, tesseract)))
                capture_index += 1
            time.sleep(0.05)
        raise AssertionError("No exact serial failure or two corroborating VGA failure frames within deadline")
    finally:
        pool.shutdown(wait=True, cancel_futures=True)
        for index, future in futures:
            frame = captured[index]
            if not future.cancelled():
                frame.update(future.result())
            else:
                ppm = output / frame["ppm"]
                tsv_path, text_path = ppm.with_suffix(".tsv"), ppm.with_suffix(".txt")
                tsv_path.write_text("", encoding="utf-8")
                text_path.write_text("", encoding="utf-8")
                frame.update(source="QEMU_VGA_TESSERACT_TSV", ocrError="Not processed before observation deadline", candidate=None,
                             ppmSha256=sha256(ppm), tsv=tsv_path.name, tsvSha256=sha256(tsv_path),
                             text=text_path.name, textSha256=sha256(text_path))
            report["frames"].append(frame)
        report["frames"].sort(key=lambda frame: frame["capturedSeconds"])
        report["visualCorroboration"] = corroborating_frames(report["frames"])
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
