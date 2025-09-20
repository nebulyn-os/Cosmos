using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cosmos.HAL.BlockDevice;
using Cosmos.System.FileSystem.Listing;

namespace Cosmos.System.FileSystem.NTFS
{
    internal class NtfsFileSystem : FileSystem
    {
        // Basic NTFS layout values (read from boot sector)
        private readonly ushort BytesPerSector;
        private readonly byte SectorsPerCluster;
        private readonly ulong MFTCluster;
        private readonly ulong MFTMirrCluster;
        private readonly int BytesPerFileRecord;
        private readonly int BytesPerIndexRecord;
        private readonly string _label;

        // $MFT data runs (VCN->LCN mappings)
        internal struct Run
        {
            public long VcnStart;   // in clusters
            public long Length;     // in clusters
            public long LcnStart;   // in clusters
        }
        private List<Run> _mftRuns = new List<Run>();

        internal NtfsFileSystem(Partition aDevice, string aRootPath, long aSize)
            : base(aDevice, aRootPath, aSize)
        {
            if (string.IsNullOrEmpty(aRootPath))
            {
                throw new ArgumentException("Root path required", nameof(aRootPath));
            }

            // Parse minimal NTFS BPB/boot sector
            byte[] boot = aDevice.NewBlockArray(1);
            aDevice.ReadBlock(0UL, 1U, ref boot);

            // Sanity check: OEM ID and signature
            if (BitConverter.ToUInt16(boot, 510) != 0xAA55)
            {
                throw new Exception("Invalid NTFS boot sector signature");
            }

            var oem = Encoding.ASCII.GetString(boot, 3, 8);
            if (oem != "NTFS    ")
            {
                throw new Exception("Not an NTFS volume");
            }

            BytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
            SectorsPerCluster = boot[0x0D];
            // MFT Logical Cluster Number at 0x30, MFTMirr at 0x38
            MFTCluster = BitConverter.ToUInt64(boot, 0x30);
            MFTMirrCluster = BitConverter.ToUInt64(boot, 0x38);
            // Clusters per file record segment (signed byte). If negative, size = 2^abs(value)
            sbyte cfr = unchecked((sbyte)boot[0x40]);
            BytesPerFileRecord = cfr < 0 ? (1 << (-cfr)) : (cfr * SectorsPerCluster * BytesPerSector);
            sbyte cir = unchecked((sbyte)boot[0x44]);
            BytesPerIndexRecord = cir < 0 ? (1 << (-cir)) : (cir * SectorsPerCluster * BytesPerSector);

            // NTFS volume label is stored in $Volume file; we cannot parse it now; default to Name
            _label = "NTFS";
        }

        public override void DisplayFileSystemInfo()
        {
            global::System.Console.WriteLine($"NTFS: {RootPath} bytes/sector={BytesPerSector}, sectors/cluster={SectorsPerCluster}, MFT LCN={MFTCluster}");
        }

        public override List<DirectoryEntry> GetDirectoryListing(DirectoryEntry baseDirectory)
        {
            ulong recordNumber;
            if (baseDirectory == null)
            {
                recordNumber = 5; // Root directory
            }
            else
            {
                NtfsDirectoryEntry nt = (NtfsDirectoryEntry)baseDirectory;
                recordNumber = nt.RecordNumber;
            }

            EnsureMftRuns();
            List<DirectoryEntry> list = EnumerateDirectory(recordNumber, baseDirectory == null ? RootPath : baseDirectory.mFullPath);
            return list;
        }

        public override DirectoryEntry GetRootDirectory()
        {
            return new NtfsDirectoryEntry(this, null, RootPath, RootPath.TrimEnd('\\', '/'), 0, DirectoryEntryTypeEnum.Directory)
            {
                RecordNumber = 5
            };
        }

        public override DirectoryEntry CreateDirectory(DirectoryEntry aParentDirectory, string aNewDirectory)
        {
            throw new NotImplementedException("NTFS write support not implemented");
        }

        public override DirectoryEntry CreateFile(DirectoryEntry aParentDirectory, string aNewFile)
        {
            throw new NotImplementedException("NTFS write support not implemented");
        }

        public override void DeleteDirectory(DirectoryEntry aPath)
        {
            throw new NotImplementedException("NTFS write support not implemented");
        }

        public override void DeleteFile(DirectoryEntry aPath)
        {
            throw new NotImplementedException("NTFS write support not implemented");
        }

        public override long AvailableFreeSpace => 0; // Unknown without parsing bitmap

        public override long TotalFreeSpace => 0; // Unknown without parsing bitmap

    public override string Type => "NTFS";

        public override string Label
        {
            get => _label;
            set => throw new NotImplementedException("Setting NTFS label not supported");
        }

        public override void Format(string aDriveFormat, bool aQuick)
        {
            throw new NotImplementedException("Formatting NTFS not supported");
        }

        internal int BytesPerCluster => BytesPerSector * SectorsPerCluster;

        // Internal helpers for use by NtfsStream (Device is protected; SectorsPerCluster is private)
        internal byte GetSectorsPerCluster()
        {
            return SectorsPerCluster;
        }

        internal byte[] NewSectorBuffer(uint sectorCount)
        {
            return Device.NewBlockArray(sectorCount);
        }

        internal void ReadSectors(ulong lba, uint count, ref byte[] buffer)
        {
            Device.ReadBlock(lba, count, ref buffer);
        }

        private void EnsureMftRuns()
        {
            if (_mftRuns.Count > 0)
            {
                return;
            }
            // Read FILE record 0 ($MFT) from physical MFT LCN, parse $DATA mapping pairs
            byte[] rec0 = ReadFileRecordRaw(0, allowDirectAtMftLcn: true);
            if (rec0 == null || rec0.Length == 0)
            {
                return;
            }
            int attrOff = BitConverter.ToUInt16(rec0, 0x14);
            while (true)
            {
                uint type = BitConverter.ToUInt32(rec0, attrOff + 0);
                if (type == 0xFFFFFFFF)
                {
                    break;
                }
                int length = BitConverter.ToInt32(rec0, attrOff + 4);
                byte nonResident = rec0[attrOff + 8];
                // Unnamed $DATA is type 0x80 with no name
                if (type == 0x80 && nonResident != 0)
                {
                    // Mapping pairs
                    ushort mappOff = BitConverter.ToUInt16(rec0, attrOff + 0x20);
                    ParseRunlist(rec0, attrOff + mappOff, _mftRuns);
                    break;
                }
                attrOff += length;
            }
        }

        private void ParseRunlist(byte[] buf, int offset, List<Run> runs)
        {
            long currentVcn = 0;
            long currentLcn = 0;
            int i = offset;
            while (i < buf.Length)
            {
                byte header = buf[i++];
                if (header == 0)
                {
                    break;
                }
                int lenLen = header & 0x0F;
                int offLen = (header >> 4) & 0x0F;
                long runLen = 0;
                long lcnDelta = 0;
                // length
                for (int b = 0; b < lenLen; b++)
                {
                    runLen |= ((long)buf[i++]) << (8 * b);
                }
                // offset (signed)
                long offVal = 0;
                for (int b = 0; b < offLen; b++)
                {
                    offVal |= ((long)buf[i++]) << (8 * b);
                }
                // sign extend if needed
                if (offLen > 0 && (buf[i - 1] & 0x80) != 0)
                {
                    // negative
                    for (int b = offLen; b < 8; b++)
                    {
                        offVal |= (-1L) << (8 * b);
                    }
                }
                lcnDelta = offVal;
                long lcnStart = currentLcn + lcnDelta;
                runs.Add(new Run { VcnStart = currentVcn, LcnStart = lcnStart, Length = runLen });
                currentVcn += runLen;
                currentLcn = lcnStart;
            }
        }

        private byte[] ReadFileRecordRaw(ulong recordNumber, bool allowDirectAtMftLcn = false)
        {
            // If we have runs for $MFT, use them to map record N to absolute LBA
            if (_mftRuns.Count > 0)
            {
                long byteOffset = (long)recordNumber * BytesPerFileRecord;
                return ReadFileBytesByRuns(_mftRuns, byteOffset, BytesPerFileRecord);
            }
            if (allowDirectAtMftLcn)
            {
                // Read directly from MFT starting LCN assuming contiguous for the first records
                ulong lba = LcnToLba(MFTCluster) + ((ulong)recordNumber * (ulong)BytesPerFileRecord) / (ulong)BytesPerSector;
                var buf = Device.NewBlockArray((uint)(BytesPerFileRecord / BytesPerSector));
                Device.ReadBlock(lba, (uint)(BytesPerFileRecord / BytesPerSector), ref buf);
                ApplyUsaFixup(buf, BytesPerFileRecord);
                return buf;
            }
            return null;
        }

        private byte[] ReadFileBytesByRuns(List<Run> runs, long fileByteOffset, int count)
        {
            byte[] outBuf = new byte[count];
            int remaining = count;
            int outOff = 0;
            long clusterSize = BytesPerCluster;
            long startVcn = (long)(fileByteOffset / clusterSize);
            long offsetInCluster = fileByteOffset % clusterSize;

            // Find run containing startVcn
            int runIdx = -1;
            for (int r = 0; r < runs.Count; r++)
            {
                Run run = runs[r];
                if (startVcn >= run.VcnStart && startVcn < run.VcnStart + run.Length)
                {
                    runIdx = r;
                    break;
                }
            }
            if (runIdx == -1)
            {
                return outBuf;
            }

            long curVcn = startVcn;
            long curOffInCluster = offsetInCluster;
            while (remaining > 0 && runIdx < runs.Count)
            {
                Run run = runs[runIdx];
                long lcn = run.LcnStart + (curVcn - run.VcnStart);
                ulong lba = LcnToLba((ulong)lcn);
                // Read one cluster at a time
                byte[] cl = Device.NewBlockArray(SectorsPerCluster);
                Device.ReadBlock(lba, SectorsPerCluster, ref cl);
                int toCopy = (int)Math.Min(remaining, BytesPerCluster - curOffInCluster);
                Buffer.BlockCopy(cl, (int)curOffInCluster, outBuf, outOff, toCopy);
                outOff += toCopy;
                remaining -= toCopy;
                curOffInCluster += toCopy;
                if (curOffInCluster >= BytesPerCluster)
                {
                    curOffInCluster = 0;
                    curVcn++;
                    if (curVcn >= run.VcnStart + run.Length)
                    {
                        runIdx++;
                    }
                }
            }
            return outBuf;
        }

        private ulong LcnToLba(ulong lcn)
        {
            return (ulong)ClusterToSector(lcn);
        }

        private ulong ClusterToSector(ulong cluster)
        {
            return (ulong)(cluster * SectorsPerCluster);
        }

        private void ApplyUsaFixup(byte[] record, int recordSize)
        {
            // Update Sequence Array fixup for FILE record
            ushort usaOff = BitConverter.ToUInt16(record, 0x04);
            ushort usaCount = BitConverter.ToUInt16(record, 0x06);
            if (usaOff == 0 || usaCount == 0)
            {
                return;
            }
            ushort usn = BitConverter.ToUInt16(record, usaOff);
            int sectorsInRecord = recordSize / BytesPerSector;
            for (int s = 1; s < usaCount; s++)
            {
                int sectorEnd = s * BytesPerSector - 2;
                ushort fixVal = BitConverter.ToUInt16(record, usaOff + 2 * s);
                // Verify signature (optional): record[sectorEnd .. +1] should equal USN
                // Replace with fixVal
                record[sectorEnd] = (byte)(fixVal & 0xFF);
                record[sectorEnd + 1] = (byte)((fixVal >> 8) & 0xFF);
            }
        }

        private List<DirectoryEntry> EnumerateDirectory(ulong recordNumber, string basePath)
        {
            List<DirectoryEntry> list = new List<DirectoryEntry>();
            byte[] rec = ReadFileRecord(recordNumber);
            if (rec == null)
            {
                return list;
            }
            int attrOff = BitConverter.ToUInt16(rec, 0x14);
            while (attrOff + 4 <= rec.Length)
            {
                uint type = BitConverter.ToUInt32(rec, attrOff + 0);
                if (type == 0xFFFFFFFF)
                {
                    break;
                }
                int length = BitConverter.ToInt32(rec, attrOff + 4);
                if (length <= 0)
                {
                    break;
                }
                byte nonResident = rec[attrOff + 8];
                if (type == 0x90 /* Index Root */ && nonResident == 0)
                {
                    // Resident content
                    int contentOff = BitConverter.ToUInt16(rec, attrOff + 0x14);
                    int contentSize = BitConverter.ToInt32(rec, attrOff + 0x10);
                    ParseIndexRoot(rec, attrOff + contentOff, contentSize, basePath, list);
                    break; // sufficient for many dirs
                }
                attrOff += length;
            }
            return list;
        }

        private void ParseIndexRoot(byte[] buf, int offset, int size, string basePath, List<DirectoryEntry> output)
        {
            // Index Root header (16 bytes) then Index Header
            // Index Header at offset+16: EntryOffset (4), IndexEntriesSize (4), Allocated (4), Flags (1), reserved (3)
            int indexHeaderOff = offset + 16;
            int entryOff = BitConverter.ToInt32(buf, indexHeaderOff + 0);
            int totalSize = BitConverter.ToInt32(buf, indexHeaderOff + 4);
            // iterate entries
            int p = indexHeaderOff + entryOff;
            int end = indexHeaderOff + totalSize;
            while (p + 16 <= end && p + 16 <= buf.Length)
            {
                ulong fileRef = BitConverter.ToUInt64(buf, p + 0);
                ushort entrySize = BitConverter.ToUInt16(buf, p + 8);
                ushort keySize = BitConverter.ToUInt16(buf, p + 10);
                uint flags = BitConverter.ToUInt32(buf, p + 12);
                bool isLast = (flags & 0x02) != 0;
                bool isSubnode = (flags & 0x01) != 0;
                if (entrySize == 0)
                {
                    break;
                }
                if (!isLast)
                {
                    // Key is FILE_NAME attribute inside the entry
                    int keyOff = p + 16;
                    if (keyOff + keySize <= buf.Length && keySize >= 66) // minimum for FILE_NAME without name
                    {
                        // FILE_NAME structure
                        // +0 ParentRef(8) +8 times(8*4) +40 AllocSize(8) +48 RealSize(8) +56 Flags(4) +60 EAs/Reparse(4) +64 NameLength(1) +65 Namespace(1) +66 Name
                        byte nameLen = buf[keyOff + 64];
                        byte nameSpace = buf[keyOff + 65];
                        int nameBytes = nameLen * 2;
                        string name = string.Empty;
                        if (keyOff + 66 + nameBytes <= buf.Length)
                        {
                            name = Encoding.Unicode.GetString(buf, keyOff + 66, nameBytes);
                        }
                        // Basic attribute flags
                        uint fileFlags = BitConverter.ToUInt32(buf, keyOff + 56);
                        bool isDir = (fileFlags & 0x10000000) != 0; // DIRECTORY bit in FILE_NAME Flags
                        // Low 48 bits of fileRef is MFT entry number
                        ulong mftEntry = fileRef & 0x0000FFFFFFFFFFFFUL;
                        string fullPath = CombinePath(basePath, name);
                        NtfsDirectoryEntry de = new NtfsDirectoryEntry(this, null, fullPath, name, 0, isDir ? DirectoryEntryTypeEnum.Directory : DirectoryEntryTypeEnum.File)
                        {
                            RecordNumber = mftEntry
                        };
                        output.Add(de);
                    }
                }
                if (isLast)
                {
                    break;
                }
                p += entrySize;
            }
        }

        private static string CombinePath(string parent, string name)
        {
            if (parent.EndsWith("\\") || parent.EndsWith("/"))
            {
                return parent + name;
            }
            return parent + "\\" + name;
        }

        private byte[] ReadFileRecord(ulong recordNumber)
        {
            byte[] raw = ReadFileRecordRaw(recordNumber);
            if (raw == null)
            {
                // Try to bootstrap runs by reading rec0
                EnsureMftRuns();
                raw = ReadFileRecordRaw(recordNumber);
            }
            if (raw != null)
            {
                ApplyUsaFixup(raw, BytesPerFileRecord);
            }
            return raw;
        }

        internal NtfsStream OpenFileStream(ulong recordNumber)
        {
            byte[] rec = ReadFileRecord(recordNumber);
            if (rec == null)
            {
                return null;
            }
            int attrOff = BitConverter.ToUInt16(rec, 0x14);
            // Iterate attributes to find unnamed $DATA
            while (true)
            {
                uint type = BitConverter.ToUInt32(rec, attrOff + 0);
                if (type == 0xFFFFFFFF)
                {
                    break;
                }
                int length = BitConverter.ToInt32(rec, attrOff + 4);
                if (length <= 0)
                {
                    break;
                }
                byte nonResident = rec[attrOff + 8];
                byte nameLen = rec[attrOff + 9];
                ushort nameOff = BitConverter.ToUInt16(rec, attrOff + 0x0A);
                if (type == 0x80) // DATA
                {
                    if (nameLen == 0)
                    {
                        if (nonResident == 0)
                        {
                            int contentSize = BitConverter.ToInt32(rec, attrOff + 0x10);
                            int contentOff = BitConverter.ToUInt16(rec, attrOff + 0x14);
                            byte[] data = new byte[contentSize];
                            Buffer.BlockCopy(rec, attrOff + contentOff, data, 0, contentSize);
                            return NtfsStream.FromResident(this, data);
                        }
                        else
                        {
                            ulong dataSize = BitConverter.ToUInt64(rec, attrOff + 0x30);
                            ushort mappOff = BitConverter.ToUInt16(rec, attrOff + 0x20);
                            List<Run> runs = new List<Run>();
                            ParseRunlist(rec, attrOff + mappOff, runs);
                            return NtfsStream.FromNonResident(this, runs, (long)dataSize);
                        }
                    }
                }
                attrOff += length;
            }
            return null;
        }
    }

    internal sealed class NtfsDirectoryEntry : DirectoryEntry
    {
        internal ulong RecordNumber;
        internal NtfsDirectoryEntry(FileSystem aFileSystem, DirectoryEntry aParent, string aFullPath, string aName, long aSize, DirectoryEntryTypeEnum aEntryType)
            : base(aFileSystem, aParent, aFullPath, aName, aSize, aEntryType)
        { }

        public override void SetName(string aName)
        {
            throw new NotImplementedException("NTFS directory entry rename not supported");
        }

        public override void SetSize(long aSize)
        {
            // Size comes from $DATA attribute; read-only for now
            mSize = aSize;
        }

        public override Stream GetFileStream()
        {
            if (mEntryType != DirectoryEntryTypeEnum.File)
            {
                return null;
            }
            NtfsFileSystem ntfs = (NtfsFileSystem)mFileSystem;
            NtfsStream s = ntfs.OpenFileStream(RecordNumber);
            return s;
        }

        public override long GetUsedSpace()
        {
            return mSize;
        }
    }

    internal sealed class NtfsStream : Stream
    {
        private readonly NtfsFileSystem _fs;
        private readonly byte[] _residentData;
        private readonly List<NtfsFileSystem.Run> _runsProxy; // not accessible; using object wrapper, so we expose methods
        private readonly List<object> _runs;
        private readonly long _length;
        private long _pos;

        private NtfsStream(NtfsFileSystem fs, byte[] resident, List<NtfsFileSystem.Run> runs, long length)
        {
            _fs = fs;
            _residentData = resident;
            _runsProxy = runs;
            _runs = null;
            _length = length;
            _pos = 0;
        }

        public static NtfsStream FromResident(NtfsFileSystem fs, byte[] data)
        {
            return new NtfsStream(fs, data, null, data?.Length ?? 0);
        }

        public static NtfsStream FromNonResident(NtfsFileSystem fs, List<NtfsFileSystem.Run> runs, long dataSize)
        {
            return new NtfsStream(fs, null, runs, dataSize);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException();
            }
            if (_pos >= _length || count == 0)
            {
                return 0;
            }

            int total = 0;
            if (_residentData != null)
            {
                int toCopy = (int)Math.Min(count, _length - _pos);
                Buffer.BlockCopy(_residentData, (int)_pos, buffer, offset, toCopy);
                _pos += toCopy;
                total += toCopy;
                return total;
            }

            // Nonresident: read via runs
            int remaining = count;
            while (remaining > 0 && _pos < _length)
            {
                // Map file byte position to cluster via runs
                long clusterSize = _fs.BytesPerCluster;
                long fileByte = _pos;
                long vcn = fileByte / clusterSize;
                long offInCluster = fileByte % clusterSize;
                NtfsFileSystem.Run? found = null;
                for (int i = 0; i < _runsProxy.Count; i++)
                {
                    var r = _runsProxy[i];
                    if (vcn >= r.VcnStart && vcn < r.VcnStart + r.Length)
                    {
                        found = r;
                        break;
                    }
                }
                if (found == null)
                {
                    break;
                }
                var run = found.Value;
                long lcn = run.LcnStart + (vcn - run.VcnStart);
                uint spc = _fs.GetSectorsPerCluster();
                ulong lba = (ulong)(lcn * spc);
                byte[] cl = _fs.NewSectorBuffer(spc);
                _fs.ReadSectors(lba, spc, ref cl);
                int toCopy = (int)Math.Min(remaining, Math.Min(_fs.BytesPerCluster - offInCluster, _length - _pos));
                Buffer.BlockCopy(cl, (int)offInCluster, buffer, offset, toCopy);
                offset += toCopy;
                remaining -= toCopy;
                _pos += toCopy;
                total += toCopy;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _length + offset,
                _ => _pos
            };
            if (newPos < 0)
            {
                throw new IOException("Negative seek");
            }
            _pos = newPos;
            return _pos;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
