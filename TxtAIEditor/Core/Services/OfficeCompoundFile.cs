using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using static TxtAIEditor.Core.Services.OfficeBinaryData;

namespace TxtAIEditor.Core.Services
{
    // MS-CFB. Sector IDs are relative to the end of the header sector.
    internal sealed class OfficeCompoundFile
    {
        private const ulong Signature = 0xE11AB1A1E011CFD0;
        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint Free = 0xFFFFFFFF;
        private const int MiniSectorSize = 64;
        private const int MiniStreamCutoff = 4096;
        private const int MaximumStreamSize = 256 * 1024 * 1024;

        private readonly Stream _file;
        private readonly CancellationToken _cancellationToken;
        private readonly int _sectorSize;
        private readonly int _sectorCount;
        private readonly uint[] _fat;
        private readonly Dictionary<string, DirectoryEntry> _streams = new(StringComparer.OrdinalIgnoreCase);
        private readonly DirectoryEntry _root;
        private readonly uint _miniFatStart;
        private readonly uint _miniFatCount;
        private uint[]? _miniFat;
        private byte[]? _miniStream;

        internal static bool HasSignature(ReadOnlySpan<byte> bytes) =>
            bytes.Length >= 8 && U64(bytes, 0) == Signature;

        internal OfficeCompoundFile(Stream file, CancellationToken cancellationToken)
        {
            _file = file;
            _cancellationToken = cancellationToken;
            Span<byte> header = stackalloc byte[512];
            file.Position = 0;
            file.ReadExactly(header);
            int majorVersion = U16(header, 26);
            int sectorShift = U16(header, 30);
            if (!HasSignature(header) || (majorVersion, sectorShift) is not ((3, 9) or (4, 12)) ||
                U16(header, 28) != 0xFFFE || U16(header, 32) != 6 || U32(header, 56) != MiniStreamCutoff)
            {
                throw new InvalidDataException("Invalid Office compound-file header.");
            }

            _sectorSize = 1 << sectorShift;
            long sectorCount = file.Length / _sectorSize - 1;
            if (sectorCount < 2 || sectorCount > int.MaxValue)
            {
                throw new InvalidDataException("Invalid Office compound-file size.");
            }

            _sectorCount = (int)sectorCount;
            _miniFatStart = U32(header, 60);
            _miniFatCount = U32(header, 64);
            _fat = ReadFat(header);
            byte[] directory = ReadChain(U32(header, 48), _fat, _sectorSize);
            _root = ReadEntry(directory, 0, majorVersion);
            if (_root.Type != 5)
            {
                throw new InvalidDataException("Missing Office compound-file root.");
            }

            var pending = new Stack<uint>();
            var visited = new HashSet<uint>();
            pending.Push(_root.Child);
            while (pending.TryPop(out uint id) && id != Free)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (id == 0 || id >= directory.Length / 128 || !visited.Add(id))
                {
                    throw new InvalidDataException("Invalid Office compound-file directory.");
                }

                DirectoryEntry entry = ReadEntry(directory, (int)id * 128, majorVersion);
                if (entry.Type == 2)
                {
                    _streams.Add(entry.Name, entry);
                }

                if (entry.Left != Free) pending.Push(entry.Left);
                if (entry.Right != Free) pending.Push(entry.Right);
            }
        }

        internal bool Contains(string name) => _streams.ContainsKey(name);

        internal byte[] ReadStream(string name)
        {
            if (!_streams.TryGetValue(name, out DirectoryEntry entry))
            {
                throw new InvalidDataException($"Missing Office stream '{name}'.");
            }

            int length = ValidateLength(entry.Length);
            if (length == 0) return Array.Empty<byte>();
            if (length >= MiniStreamCutoff) return ReadChain(entry.Start, _fat, _sectorSize, length);

            _miniFat ??= ReadAllocationTable(ReadChain(_miniFatStart, _fat, _sectorSize, SectorBytes(_miniFatCount)));
            _miniStream ??= ReadChain(_root.Start, _fat, _sectorSize, ValidateLength(_root.Length));
            return ReadChain(entry.Start, _miniFat, MiniSectorSize, length, _miniStream);
        }

        private uint[] ReadFat(ReadOnlySpan<byte> header)
        {
            uint fatCount = U32(header, 44);
            uint difatCount = U32(header, 72);
            if (fatCount == 0 || fatCount > _sectorCount || difatCount > _sectorCount)
            {
                throw new InvalidDataException("Invalid Office compound-file allocation table.");
            }

            var fatSectors = new List<uint>();
            var seen = new HashSet<uint>();
            AddFatSectors(header.Slice(76), fatCount, fatSectors, seen);
            uint next = U32(header, 68);
            byte[] sector = new byte[_sectorSize];
            var difatSectors = new HashSet<uint>();
            for (uint index = 0; index < difatCount; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!difatSectors.Add(next)) throw new InvalidDataException("Cyclic Office DIFAT.");
                ReadSector(next, sector);
                AddFatSectors(sector.AsSpan(0, _sectorSize - 4), fatCount, fatSectors, seen);
                next = U32(sector, _sectorSize - 4);
            }

            if (fatSectors.Count != fatCount || (long)fatCount * (_sectorSize / 4) < _sectorCount ||
                _sectorCount > MaximumStreamSize / sizeof(uint))
            {
                throw new InvalidDataException("Invalid Office compound-file FAT size.");
            }

            var fat = new uint[_sectorCount];
            int entry = 0;
            foreach (uint sectorId in fatSectors)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReadSector(sectorId, sector);
                for (int offset = 0; offset < _sectorSize && entry < fat.Length; offset += 4)
                {
                    fat[entry++] = U32(sector, offset);
                }
            }

            return fat;
        }

        private void AddFatSectors(ReadOnlySpan<byte> data, uint count, List<uint> sectors, HashSet<uint> seen)
        {
            for (int offset = 0; offset < data.Length && sectors.Count < count; offset += 4)
            {
                uint sector = U32(data, offset);
                if (sector == Free) continue;
                if (sector >= _sectorCount || !seen.Add(sector))
                {
                    throw new InvalidDataException("Invalid Office FAT sector.");
                }

                sectors.Add(sector);
            }
        }

        private byte[] ReadChain(uint start, uint[] allocation, int blockSize, int? length = null, byte[]? miniStream = null)
        {
            var chain = new List<uint>();
            var seen = new HashSet<uint>();
            uint sector = start;
            int? blockCount = length.HasValue ? (length.Value + blockSize - 1) / blockSize : null;
            while (sector != EndOfChain && (!blockCount.HasValue || chain.Count < blockCount.Value))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (sector >= allocation.Length || !seen.Add(sector) || chain.Count >= MaximumStreamSize / blockSize)
                {
                    throw new InvalidDataException("Invalid Office compound-file sector chain.");
                }

                chain.Add(sector);
                sector = allocation[sector];
            }

            if (blockCount.HasValue && chain.Count != blockCount.Value)
            {
                throw new InvalidDataException("Truncated Office compound-file stream.");
            }

            byte[] result = new byte[length ?? chain.Count * blockSize];
            int offset = 0;
            foreach (uint id in chain)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Span<byte> target = result.AsSpan(offset, Math.Min(blockSize, result.Length - offset));
                if (miniStream == null) ReadSector(id, target);
                else Slice(miniStream, checked((int)id * blockSize), target.Length).CopyTo(target);
                offset += target.Length;
            }

            return result;
        }

        private void ReadSector(uint id, Span<byte> target)
        {
            if (id >= _sectorCount) throw new InvalidDataException("Invalid Office compound-file sector.");
            _file.Position = (id + 1L) * _sectorSize;
            _file.ReadExactly(target);
        }

        private static uint[] ReadAllocationTable(byte[] bytes)
        {
            var table = new uint[bytes.Length / 4];
            for (int i = 0; i < table.Length; i++) table[i] = U32(bytes, i * 4);
            return table;
        }

        private int SectorBytes(uint count) => ValidateLength((ulong)count * (uint)_sectorSize);

        private static int ValidateLength(ulong length)
        {
            if (length > MaximumStreamSize) throw new InvalidDataException("An Office compound-file stream exceeds 256 MiB.");
            return (int)length;
        }

        private static DirectoryEntry ReadEntry(byte[] directory, int offset, int version)
        {
            ReadOnlySpan<byte> entry = Slice(directory, offset, 128);
            int nameLength = U16(entry, 64);
            if (nameLength < 2 || nameLength > 64 || nameLength % 2 != 0)
            {
                throw new InvalidDataException("Invalid Office compound-file stream name.");
            }

            return new DirectoryEntry(Encoding.Unicode.GetString(entry[..(nameLength - 2)]), entry[66],
                U32(entry, 68), U32(entry, 72), U32(entry, 76), U32(entry, 116),
                version == 3 ? U32(entry, 120) : U64(entry, 120));
        }

        private readonly record struct DirectoryEntry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, ulong Length);
    }
}
