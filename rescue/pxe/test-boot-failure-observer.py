#!/usr/bin/env python3
"""Strict visual criterion regressions using synthetic Tesseract TSV only."""
import csv
import importlib.util
import io
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("boot_failure_observer", Path(__file__).with_name("boot_failure_observer.py"))
observer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(observer)


def tsv(text, confidence=90):
    stream = io.StringIO()
    columns = ["level", "page_num", "block_num", "par_num", "line_num", "word_num", "left", "top", "width", "height", "conf", "text"]
    writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
    writer.writerow(columns)
    for line, content in enumerate(text.splitlines(), 1):
        left = 10
        for word_number, word in enumerate(content.split(), 1):
            width = len(word) * 8
            writer.writerow([5, 1, 1, 1, line, word_number, left, line * 20, width, 16, confidence, word])
            left += width + 8
    return stream.getvalue()


def frame(name, seconds, text=None, confidence=90):
    return {"ppm": name, "capturedSeconds": seconds,
            "candidate": observer.failure_line_from_tsv(tsv(observer.EXPECTED_FAILURE if text is None else text, confidence))}


class FailureLineTests(unittest.TestCase):
    def test_exact_complete_line(self):
        candidate = observer.failure_line_from_tsv(tsv(observer.EXPECTED_FAILURE))
        self.assertEqual(candidate["text"], observer.EXPECTED_FAILURE)
        self.assertEqual(candidate["meanConfidence"], 90)
        self.assertGreater(candidate["box"]["width"], 0)

    def test_confidence_boundary(self):
        self.assertIsNotNone(observer.failure_line_from_tsv(tsv(observer.EXPECTED_FAILURE, 80)))
        self.assertIsNone(observer.failure_line_from_tsv(tsv(observer.EXPECTED_FAILURE, 79.999)))

    def test_debug_echo_is_not_actual_output(self):
        for prefix in ["echo ", "[001a] $ echo ", "Executing: echo "]:
            with self.subTest(prefix=prefix):
                self.assertIsNone(observer.failure_line_from_tsv(tsv(prefix + observer.EXPECTED_FAILURE)))

    def test_prefix_in_another_ocr_block_on_same_row_still_fails(self):
        candidate = tsv(observer.EXPECTED_FAILURE)
        candidate += "5\t1\t2\t1\t1\t1\t0\t20\t8\t16\t90\techo\n"
        self.assertIsNone(observer.failure_line_from_tsv(candidate))

    def test_incomplete_or_different_message_fails(self):
        for text in ["Falha de boot.", "Falha de boot", observer.EXPECTED_FAILURE + " extra",
                     observer.EXPECTED_FAILURE.replace("Falha", "Falna"), observer.EXPECTED_FAILURE.lower()]:
            with self.subTest(text=text):
                self.assertIsNone(observer.failure_line_from_tsv(tsv(text)))

    def test_tcp_failure_and_firmware_menu_are_not_error_proof(self):
        for text in ["Connection reset (https://ipxe.org/0f0a6095)", "BdsDxe: starting Boot0000 UiApp", "Device Manager Boot Manager Reset"]:
            with self.subTest(text=text):
                self.assertIsNone(observer.failure_line_from_tsv(tsv(text)))

    def test_invalid_confidence_fails_closed(self):
        for confidence in [-1, 101, "nan", "inf", "malformed"]:
            with self.subTest(confidence=confidence):
                self.assertIsNone(observer.failure_line_from_tsv(tsv(observer.EXPECTED_FAILURE, confidence)))

    def test_missing_or_malformed_columns_fail_closed(self):
        self.assertIsNone(observer.failure_line_from_tsv("text\n" + observer.EXPECTED_FAILURE))
        self.assertIsNone(observer.failure_line_from_tsv(tsv(observer.EXPECTED_FAILURE).replace("\t10\t20\t", "\tinvalid\t20\t", 1)))

    def test_distinct_lines_do_not_create_a_complete_phrase(self):
        self.assertIsNone(observer.failure_line_from_tsv(tsv("Falha de boot.\nVerifique rede/servidor; nenhum disco foi modificado.")))


class CorroborationTests(unittest.TestCase):
    def test_two_independent_frames_at_boundary(self):
        self.assertEqual(observer.corroborating_frames([frame("a.ppm", 0.4), frame("b.ppm", 0.7)]), ["a.ppm", "b.ppm"])

    def test_one_frame_or_same_frame_repeated_fails(self):
        self.assertIsNone(observer.corroborating_frames([frame("a.ppm", 0.4)]))
        self.assertIsNone(observer.corroborating_frames([frame("a.ppm", 0.4), frame("a.ppm", 1.0)]))

    def test_too_close_frames_fail(self):
        self.assertIsNone(observer.corroborating_frames([frame("a.ppm", 1.0), frame("b.ppm", 1.299999)]))

    def test_weak_or_trace_counterpart_fails(self):
        self.assertIsNone(observer.corroborating_frames([frame("a.ppm", 1.0), frame("b.ppm", 3.0, confidence=79)]))
        self.assertIsNone(observer.corroborating_frames([frame("a.ppm", 1.0), frame("b.ppm", 3.0, text="echo " + observer.EXPECTED_FAILURE)]))

    def test_temporal_order_is_preserved(self):
        self.assertEqual(observer.corroborating_frames([frame("b.ppm", 3.0), frame("a.ppm", 1.0)]), ["a.ppm", "b.ppm"])


if __name__ == "__main__":
    unittest.main()
