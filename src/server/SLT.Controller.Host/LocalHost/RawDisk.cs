using System.Buffers.Binary;

using System.Security.Cryptography;

namespace Slt.Controller.LocalHost;

// Raw disk copy used by the server's local clone: copies, flushes, re-reads and compares every
// 4 MiB block by SHA256, then moves the backup GPT to the end of a target of another size.
// Works on any seekable stream: \\.\PhysicalDriveN in production, plain files in the tests.
public static class RawDisk
{
    public const int Block = 4 * 1024 * 1024;

    public static FileStream Open(string path, bool write) =>
        new(File.OpenHandle(path, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite), write ? FileAccess.ReadWrite : FileAccess.Read, 0);

    public static long CloneLength(Stream source, long sourceBytes, long targetBytes, int sector)
    {
        if (sourceBytes <= 0 || targetBytes <= 0 || sector is not (512 or 4096) || sourceBytes % sector != 0 || targetBytes % sector != 0) throw new ApiException(409, "Geometria de disco inválida.");
        if (targetBytes >= sourceBytes) return sourceBytes;
        var (end, gpt) = PartitionsEnd(source, sourceBytes, sector);
        var limit = targetBytes - (gpt ? 33L * sector : 0);
        if (end > limit) throw new ApiException(409, $"O destino é menor que o espaço ocupado pelas partições da origem ({end / 1e9:N1} GB). Reduza a última partição da origem antes de clonar.");
        var length = (end + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024);
        return length > limit ? end : length;
    }

    public static (long End, bool Gpt) PartitionsEnd(Stream f, long total, int sector)
    {
        var mbr = ReadAt(f, 0, 512);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) throw new ApiException(409, "A origem não tem tabela de partições reconhecida.");
        var header = ReadAt(f, sector, sector);
        if (header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
            var array = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
            if (count is 0 or > 1024 || size is < 128 or > 4096) throw new ApiException(409, "GPT da origem inválida.");
            var entries = ReadAt(f, array * sector, (int)(count * size));
            if (Crc32.HashToUInt32(entries) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88))) throw new ApiException(409, "CRC da GPT da origem inválido.");
            ulong last = 0;
            for (var i = 0; i < count; i++)
            {
                var e = entries.AsSpan((int)(i * size));
                if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue;
                last = Math.Max(last, BinaryPrimitives.ReadUInt64LittleEndian(e[40..]));
            }
            return (last == 0 ? 34L * sector : ((long)last + 1) * sector, true);
        }
        long end = 0;
        for (var i = 0; i < 4; i++)
        {
            var e = mbr.AsSpan(446 + i * 16, 16);
            var start = (long)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (e[4] != 0 && length > 0) end = Math.Max(end, (start + length) * sector);
        }
        if (end == 0 || end > total) throw new ApiException(409, "Tabela MBR da origem vazia ou inválida.");
        return (end, false);
    }

    public static void CopyAndVerify(Stream source, Stream target, long length, Action<long>? progress, CancellationToken cancel)
    {
        var buffer = new byte[Block];
        var hashes = new List<byte[]>();
        for (long offset = 0; offset < length; offset += Block)
        {
            cancel.ThrowIfCancellationRequested();
            var n = (int)Math.Min(Block, length - offset);
            source.Position = offset; source.ReadExactly(buffer, 0, n);
            hashes.Add(SHA256.HashData(buffer.AsSpan(0, n)));
            target.Position = offset; target.Write(buffer, 0, n);
            progress?.Invoke(offset + n);
        }
        if (target is FileStream file) file.Flush(true); else target.Flush();
        for (long offset = 0, i = 0; offset < length; offset += Block, i++)
        {
            cancel.ThrowIfCancellationRequested();
            var n = (int)Math.Min(Block, length - offset);
            target.Position = offset; target.ReadExactly(buffer, 0, n);
            if (!SHA256.HashData(buffer.AsSpan(0, n)).AsSpan().SequenceEqual(hashes[(int)i]))
                throw new ApiException(409, $"Conferência SHA256 falhou no bloco {i}: o destino não é uma cópia fiel.");
        }
    }

    // Returns false for MBR disks (nothing to move).
    public static bool RelocateGpt(Stream d, long totalBytes, int sector)
    {
        var header = ReadAt(d, sector, sector);
        if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) return false;
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (size < 92 || size > sector) throw new ApiException(409, "Cabeçalho GPT inválido.");
        if (HeaderCrc(header, size) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16))) throw new ApiException(409, "CRC do cabeçalho GPT copiado inválido.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        var array = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
        if (count is 0 or > 1024 || entrySize is < 128 or > 4096 || array != 2) throw new ApiException(409, "Array GPT não convencional.");
        var entries = ReadAt(d, array * sector, (int)(count * entrySize));
        if (Crc32.HashToUInt32(entries) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88))) throw new ApiException(409, "CRC do array GPT copiado inválido.");
        var last = totalBytes / sector - 1;
        var arraySectors = (entries.Length + sector - 1) / sector;
        var lastUsable = last - arraySectors - 1;
        for (var i = 0; i < count; i++)
        {
            var e = entries.AsSpan((int)(i * entrySize));
            if (e[..16].IndexOfAnyExcept((byte)0) >= 0 && (long)BinaryPrimitives.ReadUInt64LittleEndian(e[40..]) > lastUsable) throw new ApiException(409, "Partição ultrapassa o fim do destino.");
        }
        var primary = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(primary.AsSpan(32), (ulong)last);
        BinaryPrimitives.WriteUInt64LittleEndian(primary.AsSpan(48), (ulong)lastUsable);
        Seal(primary, size);
        var backup = (byte[])primary.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(backup.AsSpan(24), (ulong)last);
        BinaryPrimitives.WriteUInt64LittleEndian(backup.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(backup.AsSpan(72), (ulong)(last - arraySectors));
        Seal(backup, size);
        var padded = new byte[arraySectors * sector]; entries.CopyTo(padded, 0);
        WriteAt(d, (last - arraySectors) * sector, padded);
        WriteAt(d, last * sector, backup);
        WriteAt(d, sector, primary);
        var first = ReadAt(d, 0, sector);
        if (first[446 + 4] == 0xEE)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(446 + 12), (uint)Math.Min(last, uint.MaxValue));
            WriteAt(d, 0, first);
        }
        if (d is FileStream file) file.Flush(true); else d.Flush();
        VerifyGpt(d, totalBytes, sector);
        return true;
    }

    // Both copies valid, pointing at each other and at the real end of the disk.
    public static void VerifyGpt(Stream d, long totalBytes, int sector)
    {
        var last = totalBytes / sector - 1;
        (long current, long alternate, long array, uint arrayCrc) Read(long lba)
        {
            var h = ReadAt(d, lba * sector, sector);
            if (!h.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) throw new ApiException(409, $"Cabeçalho GPT ausente no LBA {lba}.");
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
            if (HeaderCrc(h, size) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16))) throw new ApiException(409, $"CRC do cabeçalho GPT inválido no LBA {lba}.");
            var array = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(72));
            var bytes = (int)(BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(80)) * BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(84)));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(88));
            if (Crc32.HashToUInt32(ReadAt(d, array * sector, bytes)) != crc) throw new ApiException(409, $"CRC do array GPT inválido no LBA {lba}.");
            return ((long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24)), (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32)), array, crc);
        }
        var a = Read(1); var b = Read(last);
        if (a.current != 1 || a.alternate != last || b.current != last || b.alternate != 1 || a.arrayCrc != b.arrayCrc) throw new ApiException(409, "GPT primária e de reserva não conferem.");
    }

    private static uint HeaderCrc(byte[] header, int size)
    {
        var copy = header.AsSpan(0, size).ToArray(); copy.AsSpan(16, 4).Clear(); return Crc32.HashToUInt32(copy);
    }
    private static void Seal(byte[] header, int size) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), HeaderCrc(header, size));
    private static byte[] ReadAt(Stream s, long offset, int count) { var data = new byte[count]; s.Position = offset; s.ReadExactly(data); return data; }
    private static void WriteAt(Stream s, long offset, byte[] data) { s.Position = offset; s.Write(data); }
}

// CRC-32 (IEEE 802.3), as used by GPT headers and partition arrays.
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    public static uint HashToUInt32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
