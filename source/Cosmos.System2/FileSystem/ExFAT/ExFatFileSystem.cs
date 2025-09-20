using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cosmos.HAL.BlockDevice;
using Cosmos.System.FileSystem.Listing;

namespace Cosmos.System.FileSystem.ExFAT
{
    internal class ExFatFileSystem : FileSystem
    {
        /// <summary>
        /// Create a new exFAT filesystem on the given partition and return a mounted instance.
        /// This writes a minimal, valid exFAT layout: VBR, FAT, Allocation Bitmap, Up-case table, and empty root directory.
        /// </summary>
        public static ExFatFileSystem CreateExFatFileSystem(Partition aDevice, string aRootPath, long aSize)
        {
            if (aDevice == null)
            {
                throw new ArgumentNullException(nameof(aDevice));
            }
            if (string.IsNullOrEmpty(aRootPath))
            {
                throw new ArgumentException("Root path required", nameof(aRootPath));
            }

            // Geometry
            ulong totalSectors = aDevice.BlockCount;
            uint bytesPerSector = (uint)aDevice.BlockSize;
            byte bytesPerSectorShift = bytesPerSector switch
            {
                512 => (byte)9,
                1024 => (byte)10,
                2048 => (byte)11,
                4096 => (byte)12,
                _ => (byte)9
            };

            // Choose cluster size based on volume size (simple heuristic)
            byte sectorsPerClusterShift;
            if (totalSectors < 1UL << 20) // < ~512MB (512B sectors)
            {
                sectorsPerClusterShift = 3; // 8 sectors
            }
            else if (totalSectors < 1UL << 22) // < ~2GB
            {
                sectorsPerClusterShift = 5; // 32 sectors
            }
            else if (totalSectors < 1UL << 24) // < ~8GB
            {
                sectorsPerClusterShift = 6; // 64 sectors
            }
            else
            {
                sectorsPerClusterShift = 7; // 128 sectors
            }

            uint sectorsPerCluster = (uint)(1u << sectorsPerClusterShift);
            uint bytesPerCluster = bytesPerSector * sectorsPerCluster;

            // exFAT layout: [VBR=1] [FAT] [ClusterHeap]
            uint numberOfFats = 1;
            uint fatOffset = 1; // start after VBR
            uint fatLength = 0; // to compute
            uint clusterHeapOffset = 0; // to compute
            uint clusterCount = 0; // to compute

            // Iterate once or twice to converge FAT length and cluster count
            for (int i = 0; i < 4; i++)
            {
                clusterHeapOffset = fatOffset + fatLength;
                ulong clusterHeapSectors = totalSectors > clusterHeapOffset ? totalSectors - clusterHeapOffset : 0;
                uint newClusterCount = (uint)(clusterHeapSectors / sectorsPerCluster);
                if (newClusterCount < 8)
                {
                    // ensure some minimal cluster count
                    newClusterCount = 8;
                }
                uint fatEntries = newClusterCount + 2; // include clusters starting at 2
                uint newFatLength = (uint)(((ulong)fatEntries * 4 + bytesPerSector - 1) / bytesPerSector);
                if (newFatLength == fatLength && newClusterCount == clusterCount)
                {
                    break;
                }
                fatLength = newFatLength;
                clusterCount = newClusterCount;
            }

            // Allocate clusters for system files
            uint nextCluster = 2;
            uint rootDirCluster = nextCluster++;

            // Allocation Bitmap length (in bytes) and clusters needed
            ulong bitmapBytes = (ulong)((clusterCount + 7) / 8);
            uint bitmapClusters = (uint)((bitmapBytes + bytesPerCluster - 1) / bytesPerCluster);
            if (bitmapClusters == 0)
            {
                bitmapClusters = 1;
            }
            uint bitmapFirstCluster = nextCluster;
            nextCluster += bitmapClusters;

            // Up-case table: allocate 1 cluster with trivial data
            uint upcaseFirstCluster = nextCluster++;
            ulong upcaseBytes = bytesPerCluster; // simple 1-cluster table

            // Percent in use (rough approximation)
            byte percentInUse = clusterCount == 0 ? (byte)0 : (byte)Math.Min(100, (int)((nextCluster - 2) * 100 / clusterCount));

            // Build and write VBR (sector 0)
            byte[] vbr = aDevice.NewBlockArray(1);
            for (int i = 0; i < vbr.Length; i++)
            {
                vbr[i] = 0;
            }
            vbr[0] = 0xEB; vbr[1] = 0x76; vbr[2] = 0x90; // JMP short, NOP
            Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(vbr, 3);
            // PartitionOffset (0) @0x40
            // VolumeLength (sectors) @0x48 (8 bytes)
            BitConverter.GetBytes(totalSectors).CopyTo(vbr, 0x48);
            // FATOffset @0x50, FATLength @0x54
            BitConverter.GetBytes(fatOffset).CopyTo(vbr, 0x50);
            BitConverter.GetBytes(fatLength).CopyTo(vbr, 0x54);
            // ClusterHeapOffset @0x58, ClusterCount @0x5C
            BitConverter.GetBytes(clusterHeapOffset).CopyTo(vbr, 0x58);
            BitConverter.GetBytes(clusterCount).CopyTo(vbr, 0x5C);
            // RootDirCluster @0x60
            BitConverter.GetBytes(rootDirCluster).CopyTo(vbr, 0x60);
            // VolumeSerialNumber @0x64
            BitConverter.GetBytes((uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF)).CopyTo(vbr, 0x64);
            // FileSystemRevision @0x68 (1.0)
            BitConverter.GetBytes((ushort)0x0100).CopyTo(vbr, 0x68);
            // VolumeFlags @0x6A
            BitConverter.GetBytes((ushort)0).CopyTo(vbr, 0x6A);
            // BytesPerSectorShift @0x6C, SectorsPerClusterShift @0x6D, NumberOfFats @0x6E, DriveSelect @0x6F
            vbr[0x6C] = bytesPerSectorShift;
            vbr[0x6D] = sectorsPerClusterShift;
            vbr[0x6E] = (byte)numberOfFats;
            vbr[0x6F] = 0x80; // fixed disk
            // PercentInUse @0x70
            vbr[0x70] = percentInUse;
            // Signature 0xAA55
            vbr[510] = 0x55; vbr[511] = 0xAA;
            aDevice.WriteBlock(0, 1, ref vbr);

            // Write FAT
            uint fatEntriesCount = clusterCount + 2;
            byte[] fatBytes = new byte[fatLength * bytesPerSector];
            // Initialize FAT entries to 0
            // Mark used clusters
            void SetFat(uint cluster, uint value)
            {
                if (cluster >= fatEntriesCount)
                {
                    return;
                }
                int off = (int)(cluster * 4);
                fatBytes[off + 0] = (byte)(value & 0xFF);
                fatBytes[off + 1] = (byte)((value >> 8) & 0xFF);
                fatBytes[off + 2] = (byte)((value >> 16) & 0xFF);
                fatBytes[off + 3] = (byte)((value >> 24) & 0xFF);
            }

            // Root dir single cluster EOC
            SetFat(rootDirCluster, 0xFFFFFFFF);
            // Allocation bitmap chain
            for (uint i = 0; i < bitmapClusters; i++)
            {
                uint c = bitmapFirstCluster + i;
                uint val = (i == bitmapClusters - 1) ? 0xFFFFFFFFu : (c + 1);
                SetFat(c, val);
            }
            // Up-case single cluster EOC
            SetFat(upcaseFirstCluster, 0xFFFFFFFF);

            // Write FAT sectors
            for (uint s = 0; s < fatLength; s++)
            {
                byte[] sec = new byte[bytesPerSector];
                Buffer.BlockCopy(fatBytes, (int)(s * bytesPerSector), sec, 0, (int)bytesPerSector);
                aDevice.WriteBlock(fatOffset + s, 1, ref sec);
            }

            // Write Allocation Bitmap data across its cluster chain
            byte[] bmData = new byte[bitmapClusters * bytesPerCluster];
            // Mark allocated clusters in bitmap: root, bitmap chain, up-case
            void SetBit(uint cluster)
            {
                if (cluster < 2)
                {
                    return;
                }
                uint bitIndex = cluster - 2;
                uint byteIndex = bitIndex / 8;
                int bitInByte = (int)(bitIndex % 8);
                if (byteIndex < bmData.Length)
                {
                    bmData[byteIndex] |= (byte)(1 << bitInByte);
                }
            }
            SetBit(rootDirCluster);
            for (uint i = 0; i < bitmapClusters; i++)
            {
                SetBit(bitmapFirstCluster + i);
            }
            SetBit(upcaseFirstCluster);

            // Write bmData to cluster heap
            for (uint i = 0; i < bitmapClusters; i++)
            {
                uint cl = bitmapFirstCluster + i;
                ulong lba = clusterHeapOffset + (ulong)(cl - 2) * sectorsPerCluster;
                byte[] clBuf = new byte[bytesPerCluster];
                Buffer.BlockCopy(bmData, (int)(i * bytesPerCluster), clBuf, 0, (int)bytesPerCluster);
                // Write cluster sectors
                for (uint j = 0; j < sectorsPerCluster; j++)
                {
                    byte[] sec = new byte[bytesPerSector];
                    Buffer.BlockCopy(clBuf, (int)(j * bytesPerSector), sec, 0, (int)bytesPerSector);
                    aDevice.WriteBlock(lba + j, 1, ref sec);
                }
            }

            // Write Up-case table (trivial zeroed table)
            {
                uint cl = upcaseFirstCluster;
                ulong lba = clusterHeapOffset + (ulong)(cl - 2) * sectorsPerCluster;
                byte[] clBuf = new byte[bytesPerCluster];
                for (int i = 0; i < clBuf.Length; i++)
                {
                    clBuf[i] = 0;
                }
                for (uint j = 0; j < sectorsPerCluster; j++)
                {
                    byte[] sec = new byte[bytesPerSector];
                    Buffer.BlockCopy(clBuf, (int)(j * bytesPerSector), sec, 0, (int)bytesPerSector);
                    aDevice.WriteBlock(lba + j, 1, ref sec);
                }
            }

            // Write Root Directory with 0x81 Allocation Bitmap and 0x82 Up-case entries
            {
                byte[] dir = new byte[bytesPerCluster];
                for (int i = 0; i < dir.Length; i++)
                {
                    dir[i] = 0;
                }
                // Allocation Bitmap entry at first slot
                int off = 0;
                dir[off + 0] = 0x81; // type
                dir[off + 1] = 0x00; // bitmap id (FAT 0)
                // off+2..19 reserved
                Array.Copy(BitConverter.GetBytes(bitmapFirstCluster), 0, dir, off + 20, 4);
                Array.Copy(BitConverter.GetBytes(bitmapBytes), 0, dir, off + 24, 8);
                // Up-case entry at second slot
                off = 32;
                dir[off + 0] = 0x82;
                // off+1..19 reserved (checksum omitted)
                Array.Copy(BitConverter.GetBytes(upcaseFirstCluster), 0, dir, off + 20, 4);
                Array.Copy(BitConverter.GetBytes(upcaseBytes), 0, dir, off + 24, 8);

                uint cl = rootDirCluster;
                ulong lba = clusterHeapOffset + (ulong)(cl - 2) * sectorsPerCluster;
                for (uint j = 0; j < sectorsPerCluster; j++)
                {
                    byte[] sec = new byte[bytesPerSector];
                    Buffer.BlockCopy(dir, (int)(j * bytesPerSector), sec, 0, (int)bytesPerSector);
                    aDevice.WriteBlock(lba + j, 1, ref sec);
                }
            }

            // Return the mounted FS instance
            return new ExFatFileSystem(aDevice, aRootPath, aSize);
        }
        // Minimal fields from exFAT VBR
        private readonly ushort BytesPerSector;
        private readonly byte SectorsPerCluster;
        private readonly byte NumberOfFats;
        private readonly ushort VolumeFlags;
        private readonly int ActiveFat; // 0 or 1
        private readonly uint FATOffset;
        private readonly uint FATLength;
        private readonly uint ClusterHeapOffset;
        private readonly uint ClusterCount;
        private readonly uint RootDirCluster;
        private readonly string _label;
    internal uint BytesPerCluster { get; }

        // Allocation Bitmap and Up-case Table metadata
        private struct BitmapInfo
        {
            public byte Identifier; // 0 or 1 (which FAT copy)
            public uint FirstCluster;
            public ulong DataLength;
            public List<uint> Chain;
        }
        private readonly List<BitmapInfo> _bitmaps = new List<BitmapInfo>(2);
        private uint _upcaseFirstCluster;
        private ulong _upcaseLength;

        internal ExFatFileSystem(Partition aDevice, string aRootPath, long aSize)
            : base(aDevice, aRootPath, aSize)
        {
            if (string.IsNullOrEmpty(aRootPath))
            {
                throw new ArgumentException("Root path required", nameof(aRootPath));
            }

            var boot = aDevice.NewBlockArray(1);
            aDevice.ReadBlock(0UL, 1U, ref boot);

            if (BitConverter.ToUInt16(boot, 510) != 0xAA55)
            {
                throw new Exception("Invalid exFAT boot signature");
            }

            var oem = Encoding.ASCII.GetString(boot, 3, 8);
            if (oem != "EXFAT   ")
            {
                throw new Exception("Not an exFAT volume");
            }

            // exFAT BPB fields (per spec)
            FATOffset = BitConverter.ToUInt32(boot, 0x50);
            FATLength = BitConverter.ToUInt32(boot, 0x54);
            ClusterHeapOffset = BitConverter.ToUInt32(boot, 0x58);
            ClusterCount = BitConverter.ToUInt32(boot, 0x5C);
            RootDirCluster = BitConverter.ToUInt32(boot, 0x60);
            BytesPerSector = (ushort)(1U << boot[0x6C]); // BytesPerSectorShift
            SectorsPerCluster = (byte)(1U << boot[0x6D]); // SectorsPerClusterShift
            VolumeFlags = BitConverter.ToUInt16(boot, 0x6A);
            NumberOfFats = boot[0x6E];
            ActiveFat = (VolumeFlags & 0x0001) != 0 ? 1 : 0; // bit0 selects active FAT index

            _label = "exFAT";
            BytesPerCluster = (uint)BytesPerSector * SectorsPerCluster;

            // Discover Allocation Bitmap(s) and Up-case Table in the root directory
            try
            {
                ScanVolumeMetadata();
            }
            catch
            {
                // Non-fatal: continue without bitmap/upcase cache
            }
        }

        public override void DisplayFileSystemInfo()
        {
            global::System.Console.WriteLine($"exFAT: {RootPath} bytes/sector={BytesPerSector}, sectors/cluster={SectorsPerCluster}, clusters={ClusterCount}");
        }

        public override List<DirectoryEntry> GetDirectoryListing(DirectoryEntry baseDirectory)
        {
            List<DirectoryEntry> list = new List<DirectoryEntry>();
            ExFatDirectoryEntry dir = baseDirectory as ExFatDirectoryEntry;
            uint dirCluster = dir == null ? RootDirCluster : dir.FirstCluster;
            string basePath = baseDirectory == null ? RootPath : baseDirectory.mFullPath;
            foreach (var item in EnumerateDirectory(dirCluster, basePath))
            {
                list.Add(item);
            }
            return list;
        }

        public override DirectoryEntry GetRootDirectory()
        {
            return new ExFatDirectoryEntry(this, null, RootPath, RootPath.TrimEnd('\\', '/'), 0, DirectoryEntryTypeEnum.Directory)
            {
                FirstCluster = RootDirCluster,
                DirCluster = RootDirCluster,
                EntryIndex = 0,
                SecondaryCount = 0
            };
        }

        public override DirectoryEntry CreateDirectory(DirectoryEntry aParentDirectory, string aNewDirectory)
        {
            if (aParentDirectory == null)
            {
                throw new ArgumentNullException(nameof(aParentDirectory));
            }
            if (string.IsNullOrWhiteSpace(aNewDirectory))
            {
                throw new ArgumentNullException(nameof(aNewDirectory));
            }

            ExFatDirectoryEntry parent = (ExFatDirectoryEntry)aParentDirectory;
            var newCluster = AllocateClusterChain(1)[0];
            ZeroCluster(newCluster);

            var nameEntries = GetFileNameEntryCount(aNewDirectory);
            EnsureFreeDirEntries(parent.FirstCluster, (uint)(1 + 1 + nameEntries), out uint targetCluster, out int startIndex);
            WriteFileDirectorySet(targetCluster, startIndex, aNewDirectory, true, newCluster, 0);

            var fullPath = CombinePath(parent.mFullPath, aNewDirectory);
            return new ExFatDirectoryEntry(this, parent, fullPath, aNewDirectory, 0, DirectoryEntryTypeEnum.Directory)
            {
                FirstCluster = newCluster,
                DirCluster = targetCluster,
                EntryIndex = (uint)startIndex,
                SecondaryCount = (uint)(1 + nameEntries),
                NameEntryCount = (uint)nameEntries
            };
        }

        public override DirectoryEntry CreateFile(DirectoryEntry aParentDirectory, string aNewFile)
        {
            if (aParentDirectory == null)
            {
                throw new ArgumentNullException(nameof(aParentDirectory));
            }
            if (string.IsNullOrWhiteSpace(aNewFile))
            {
                throw new ArgumentNullException(nameof(aNewFile));
            }

            ExFatDirectoryEntry parent = (ExFatDirectoryEntry)aParentDirectory;
            uint firstCluster = 0; // none allocated yet
            ulong dataLen = 0;

            var nameEntries = GetFileNameEntryCount(aNewFile);
            EnsureFreeDirEntries(parent.FirstCluster, (uint)(1 + 1 + nameEntries), out uint targetCluster, out int startIndex);
            WriteFileDirectorySet(targetCluster, startIndex, aNewFile, false, firstCluster, dataLen);

            var fullPath = CombinePath(parent.mFullPath, aNewFile);
            return new ExFatDirectoryEntry(this, parent, fullPath, aNewFile, 0, DirectoryEntryTypeEnum.File)
            {
                FirstCluster = firstCluster,
                Length = 0,
                DirCluster = targetCluster,
                EntryIndex = (uint)startIndex,
                SecondaryCount = (uint)(1 + nameEntries),
                NameEntryCount = (uint)nameEntries
            };
        }

        public override void DeleteDirectory(DirectoryEntry aPath)
        {
            ExFatDirectoryEntry entry = (ExFatDirectoryEntry)aPath;
            var items = EnumerateDirectory(entry.FirstCluster, entry.mFullPath);
            foreach (var it in items)
            {
                if (it.mEntryType != DirectoryEntryTypeEnum.Unknown)
                {
                    throw new IOException("Directory not empty");
                }
            }
            if (entry.FirstCluster >= 2)
            {
                FreeClusterChain(entry.FirstCluster);
            }
            MarkDirEntryFree(entry.DirCluster, (int)entry.EntryIndex, (int)(1 + entry.SecondaryCount));
        }

        public override void DeleteFile(DirectoryEntry aPath)
        {
            ExFatDirectoryEntry entry = (ExFatDirectoryEntry)aPath;
            if (entry.FirstCluster >= 2)
            {
                FreeClusterChain(entry.FirstCluster);
            }
            MarkDirEntryFree(entry.DirCluster, (int)entry.EntryIndex, (int)(1 + entry.SecondaryCount));
        }

        public override long AvailableFreeSpace
        {
            get
            {
                long freeClusters = 0;
                for (uint c = 2; c < ClusterCount + 2; c++)
                {
                    if (GetFatEntry(c) == 0)
                    {
                        freeClusters++;
                    }
                }
                return freeClusters * BytesPerCluster;
            }
        }
        public override long TotalFreeSpace => AvailableFreeSpace;
        public override string Type => "exFAT";
        public override string Label
        {
            get => _label;
            set => throw new NotImplementedException("Setting exFAT label not supported");
        }

        public override void Format(string aDriveFormat, bool aQuick)
        {
            throw new NotImplementedException("Formatting exFAT not supported");
        }

        // Helpers
        private ulong ClusterToSector(uint cluster) => ClusterHeapOffset + (ulong)(cluster - 2) * SectorsPerCluster;

        internal byte[] ReadCluster(uint cluster)
        {
            var buf = Device.NewBlockArray(SectorsPerCluster);
            var lba = ClusterToSector(cluster);
            Device.ReadBlock(lba, SectorsPerCluster, ref buf);
            return buf;
        }

        internal void WriteCluster(uint cluster, byte[] data)
        {
            var lba = ClusterToSector(cluster);
            Device.WriteBlock(lba, SectorsPerCluster, ref data);
        }

        private void ZeroCluster(uint cluster)
        {
            var buf = Device.NewBlockArray(SectorsPerCluster);
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = 0;
            }
            WriteCluster(cluster, buf);
        }

        private uint GetFatEntry(uint cluster)
        {
            ulong fatFirstSector = FATOffset;
            ulong entriesPerSector = (ulong)BytesPerSector / 4;
            ulong index = cluster;
            ulong sector = fatFirstSector + (index / entriesPerSector);
            int offset = (int)((index % entriesPerSector) * 4);
            var buf = Device.NewBlockArray(1);
            Device.ReadBlock(sector, 1, ref buf);
            return BitConverter.ToUInt32(buf, offset);
        }

        internal void SetFatEntry(uint cluster, uint value)
        {
            ulong fatFirstSector = FATOffset;
            ulong entriesPerSector = (ulong)BytesPerSector / 4;
            ulong index = cluster;
            ulong sector = fatFirstSector + (index / entriesPerSector);
            int offset = (int)((index % entriesPerSector) * 4);
            var buf = Device.NewBlockArray(1);
            Device.ReadBlock(sector, 1, ref buf);
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, buf, offset, 4);
            Device.WriteBlock(sector, 1, ref buf);
        }

        internal List<uint> GetClusterChain(uint firstCluster)
        {
            List<uint> chain = new List<uint>();
            if (firstCluster < 2)
            {
                return chain;
            }
            uint c = firstCluster;
            while (c >= 2 && c != 0xFFFFFFFF)
            {
                chain.Add(c);
                uint next = GetFatEntry(c);
                if (next == 0 || next == 0xFFFFFFFF || next >= 0xFFFFFFF0)
                {
                    break;
                }
                c = next;
            }
            return chain;
        }

        internal uint[] AllocateClusterChain(int count)
        {
            List<uint> allocated = new List<uint>(count);
            uint prev = 0;
            for (int i = 0; i < count; i++)
            {
                uint free = FindFreeCluster();
                if (free == 0)
                {
                    throw new IOException("No free clusters");
                }
                SetFatEntry(free, 0xFFFFFFFF);
                // Mark allocation bitmap
                SetAllocationBitmap(free, true);
                if (prev != 0)
                {
                    SetFatEntry(prev, free);
                }
                ZeroCluster(free);
                allocated.Add(free);
                prev = free;
            }
            return allocated.ToArray();
        }

        internal void FreeClusterChain(uint firstCluster)
        {
            uint c = firstCluster;
            while (c >= 2 && c != 0xFFFFFFFF)
            {
                uint next = GetFatEntry(c);
                SetFatEntry(c, 0);
                // Clear allocation bitmap
                SetAllocationBitmap(c, false);
                if (next == 0xFFFFFFFF || next == 0 || next >= 0xFFFFFFF0)
                {
                    break;
                }
                c = next;
            }
        }

        private uint FindFreeCluster()
        {
            for (uint c = 2; c < ClusterCount + 2; c++)
            {
                if (GetFatEntry(c) == 0)
                {
                    return c;
                }
            }
            return 0;
        }

        private IEnumerable<ExFatDirectoryEntry> EnumerateDirectory(uint dirCluster, string basePath)
        {
            List<ExFatDirectoryEntry> result = new List<ExFatDirectoryEntry>();
            List<uint> chain = GetClusterChain(dirCluster);
            if (chain.Count == 0)
            {
                chain.Add(dirCluster);
            }

            // Cache cluster data for this directory to allow safe cross-boundary parsing
            List<byte[]> clusters = new List<byte[]>(chain.Count);
            for (int ci = 0; ci < chain.Count; ci++)
            {
                clusters.Add(ReadCluster(chain[ci]));
            }

            int entrySize = 32;
            int entriesPerCluster = clusters[0].Length / entrySize;
            int totalEntries = entriesPerCluster * clusters.Count;

            // Helper to get an entry byte from the logical stream
            byte GetByteAt(int globalEntryIndex, int entryByteOffset)
            {
                int clusterIndex = globalEntryIndex / entriesPerCluster;
                int entryIndexInCluster = globalEntryIndex % entriesPerCluster;
                int off = entryIndexInCluster * entrySize + entryByteOffset;
                return clusters[clusterIndex][off];
            }
            void CopyBytesAt(int globalEntryIndex, int entryByteOffset, byte[] dst, int dstOffset, int count)
            {
                int clusterIndex = globalEntryIndex / entriesPerCluster;
                int entryIndexInCluster = globalEntryIndex % entriesPerCluster;
                int off = entryIndexInCluster * entrySize + entryByteOffset;
                Buffer.BlockCopy(clusters[clusterIndex], off, dst, dstOffset, count);
            }

            for (int i = 0; i < totalEntries; i++)
            {
                byte type = GetByteAt(i, 0);
                if (type == 0x00)
                {
                    // free/end marker; skip
                    continue;
                }
                if ((type & 0x80) == 0)
                {
                    // not in-use
                    continue;
                }
                if (type == 0x85)
                {
                    try
                    {
                        byte secondaryCount = GetByteAt(i, 1);
                        // Primary attributes
                        byte[] attrBuf = new byte[2];
                        CopyBytesAt(i, 4, attrBuf, 0, 2);
                        ushort fileAttr = BitConverter.ToUInt16(attrBuf, 0);

                        uint streamFirstCluster = 0;
                        ulong dataLen = 0;
                        int nameLen = 0;
                        StringBuilder nameBuilder = new StringBuilder();
                        int streamIndex = -1;
                        int nameStartIndex = -1;

                        for (int s = 1; s <= secondaryCount && (i + s) < totalEntries; s++)
                        {
                            byte t2 = GetByteAt(i + s, 0);
                            if (t2 == 0xC0)
                            {
                                nameLen = GetByteAt(i + s, 3);
                                byte[] tmp = new byte[4];
                                CopyBytesAt(i + s, 0x14, tmp, 0, 4);
                                streamFirstCluster = BitConverter.ToUInt32(tmp, 0);
                                byte[] dl = new byte[8];
                                CopyBytesAt(i + s, 0x18, dl, 0, 8);
                                dataLen = BitConverter.ToUInt64(dl, 0);
                                streamIndex = i + s;
                            }
                            else if (t2 == 0xC1)
                            {
                                if (nameStartIndex == -1)
                                {
                                    nameStartIndex = i + s;
                                }
                                // 15 UTF-16 code units per name entry
                                for (int j = 0; j < 15; j++)
                                {
                                    byte[] chb = new byte[2];
                                    CopyBytesAt(i + s, 2 + j * 2, chb, 0, 2);
                                    ushort ch = BitConverter.ToUInt16(chb, 0);
                                    if (ch == 0x0000)
                                    {
                                        break;
                                    }
                                    nameBuilder.Append((char)ch);
                                }
                            }
                        }

                        // We require a valid StreamExt entry to avoid malformed records
                        if (streamIndex == -1)
                        {
                            continue;
                        }

                        string name = nameBuilder.ToString();
                        if (nameLen > 0 && name.Length > nameLen)
                        {
                            name = name.Substring(0, nameLen);
                        }
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }
                        name = SanitizeName(name);

                        bool isDir = (fileAttr & 0x10) != 0;

                        // Compute the cluster and entry index for the primary to store for potential later updates
                        int primaryClusterIndex = i / entriesPerCluster;
                        int primaryEntryIndexInCluster = i % entriesPerCluster;
                        uint primaryEntryCluster = chain[primaryClusterIndex];

                        string fullPath = CombinePath(basePath, name);
                        ExFatDirectoryEntry entry = new ExFatDirectoryEntry(this, null, fullPath, name, (long)dataLen, isDir ? DirectoryEntryTypeEnum.Directory : DirectoryEntryTypeEnum.File)
                        {
                            FirstCluster = streamFirstCluster,
                            Length = dataLen,
                            Attributes = fileAttr,
                            DirCluster = primaryEntryCluster,
                            EntryIndex = (uint)primaryEntryIndexInCluster,
                            SecondaryCount = secondaryCount,
                            StreamEntryIndex = streamIndex % entriesPerCluster, // NOTE: stream/name may be in later cluster; write ops assume same cluster
                            NameEntryStartIndex = (nameStartIndex >= 0) ? (nameStartIndex % entriesPerCluster) : -1,
                            NameEntryCount = (uint)Math.Max(0, secondaryCount - 1)
                        };
                        result.Add(entry);
                        i += secondaryCount;
                    }
                    catch
                    {
                        // Skip malformed or partially read entries
                        continue;
                    }
                }
            }
            return result;
        }

        private string GetDirPathByCluster(uint dirCluster)
        {
            if (dirCluster == RootDirCluster)
            {
                return RootPath;
            }
            return RootPath;
        }

        private static string CombinePath(string parent, string name)
        {
            if (parent.EndsWith("\\") || parent.EndsWith("/"))
            {
                return parent + name;
            }
            return parent + "\\" + name;
        }

        private static int GetFileNameEntryCount(string name)
        {
            int u16len = name.Length;
            return (u16len + 14) / 15;
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                // Keep printable ASCII only; map others to '?'
                sb.Append((ch >= 0x20 && ch <= 0x7E) ? ch : '?');
            }
            return sb.ToString();
        }

        private bool EnsureFreeDirEntries(uint dirCluster, uint needed, out uint targetCluster, out int startIndex)
        {
            var chain = GetClusterChain(dirCluster);
            if (chain.Count == 0)
            {
                chain.Add(dirCluster);
            }
            foreach (var cl in chain)
            {
                var data = ReadCluster(cl);
                int entries = data.Length / 32;
                int run = 0;
                for (int i = 0; i < entries; i++)
                {
                    byte t = data[i * 32];
                    if (t == 0x00 || (t & 0x80) == 0)
                    {
                        run++;
                        if (run >= needed)
                        {
                            targetCluster = cl;
                            startIndex = i - (int)needed + 1;
                            return true;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }
            }
            var added = AllocateClusterChain(1);
            LinkClusterToDirectory(dirCluster, added[0]);
            targetCluster = added[0];
            startIndex = 0;
            return true;
        }

        private void LinkClusterToDirectory(uint dirFirstCluster, uint newCluster)
        {
            var chain = GetClusterChain(dirFirstCluster);
            if (chain.Count == 0)
            {
                SetFatEntry(dirFirstCluster, newCluster);
                SetFatEntry(newCluster, 0xFFFFFFFF);
            }
            else
            {
                uint last = chain[chain.Count - 1];
                SetFatEntry(last, newCluster);
                SetFatEntry(newCluster, 0xFFFFFFFF);
            }
        }

        private void WriteFileDirectorySet(uint cluster, int startIndex, string name, bool isDirectory, uint firstCluster, ulong dataLen)
        {
            var buf = ReadCluster(cluster);
            int primaryOff = startIndex * 32;
            int nameEntries = GetFileNameEntryCount(name);
            byte secondaryCount = (byte)(1 + nameEntries);

            // Zero-initialize the directory entry set to satisfy exFAT reserved fields
            Array.Clear(buf, primaryOff, 32);
            int streamOff = (startIndex + 1) * 32;
            Array.Clear(buf, streamOff, 32);
            for (int ne = 0; ne < nameEntries; ne++)
            {
                int nameEntryOffClear = (startIndex + 2 + ne) * 32;
                Array.Clear(buf, nameEntryOffClear, 32);
            }

            buf[primaryOff + 0] = 0x85;
            buf[primaryOff + 1] = secondaryCount;
            ushort attrs = (ushort)(isDirectory ? 0x10 : 0x20);
            var attrBytes = BitConverter.GetBytes(attrs);
            Buffer.BlockCopy(attrBytes, 0, buf, primaryOff + 4, 2);
            // Reserved1 at +6..+7 left zero

            buf[streamOff + 0] = 0xC0;
            // GeneralSecondaryFlags at +1 left zero
            buf[streamOff + 3] = (byte)name.Length;
            // NameHash (simple ASCII upper-case based per-spec rolling checksum)
            ushort nameHash = ComputeNameHash(name);
            var nh = BitConverter.GetBytes(nameHash);
            Buffer.BlockCopy(nh, 0, buf, streamOff + 0x04, 2);
            // Reserved2 at +6..+7 left zero
            var vdl = BitConverter.GetBytes(dataLen);
            Buffer.BlockCopy(vdl, 0, buf, streamOff + 0x08, 8);
            // Reserved3 at +16..+19 left zero
            var fcl = BitConverter.GetBytes(firstCluster);
            Buffer.BlockCopy(fcl, 0, buf, streamOff + 0x14, 4);
            var dl = BitConverter.GetBytes(dataLen);
            Buffer.BlockCopy(dl, 0, buf, streamOff + 0x18, 8);

            var nameU16 = name.ToCharArray();
            int processed = 0;
            for (int ne = 0; ne < nameEntries; ne++)
            {
                int nameEntryOff = (startIndex + 2 + ne) * 32;
                buf[nameEntryOff + 0] = 0xC1;
                for (int j = 0; j < 15; j++)
                {
                    ushort ch = 0;
                    if (processed < nameU16.Length)
                    {
                        ch = nameU16[processed++];
                    }
                    var chb = BitConverter.GetBytes(ch);
                    Buffer.BlockCopy(chb, 0, buf, nameEntryOff + 2 + j * 2, 2);
                }
            }

            // Initialize timestamps on creation (Creation/LastModified/LastAccessed + 10ms increments + UTC offsets)
            DateTime now = DateTime.Now;
            DateTime epoch = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            // Avoid ToUniversalTime to prevent TimeZoneInfo usage in Cosmos; treat current ticks as UTC
            DateTime utc = new DateTime(now.Ticks, DateTimeKind.Utc);
            double totalSec = (utc - epoch).TotalSeconds;
            if (totalSec < 0)
            {
                totalSec = 0;
            }
            if (totalSec > uint.MaxValue)
            {
                totalSec = uint.MaxValue;
            }
            uint ts = (uint)totalSec;
            var tsb = BitConverter.GetBytes(ts);
            Buffer.BlockCopy(tsb, 0, buf, primaryOff + 8, 4);   // CreateTimestamp
            Buffer.BlockCopy(tsb, 0, buf, primaryOff + 12, 4);  // LastModifiedTimestamp
            Buffer.BlockCopy(tsb, 0, buf, primaryOff + 16, 4);  // LastAccessedTimestamp
            int tenMs = (int)((now.Millisecond % 1000) / 10);
            if (tenMs < 0)
            {
                tenMs = 0;
            }
            if (tenMs > 199)
            {
                tenMs = 199;
            }
            buf[primaryOff + 20] = (byte)tenMs; // Create10msIncrement
            buf[primaryOff + 21] = (byte)tenMs; // LastModified10msIncrement
            // Store UTC offset as zero to avoid TimeZoneInfo in Cosmos
            buf[primaryOff + 22] = 0; // CreateUtcOffset
            buf[primaryOff + 23] = 0; // LastModifiedUtcOffset
            buf[primaryOff + 24] = 0; // LastAccessedUtcOffset

            // Compute and write Directory Entry Set Checksum (primary bytes 2..3)
            ushort setCk = ComputeEntrySetChecksum(buf, primaryOff, 1 + secondaryCount);
            var scb = BitConverter.GetBytes(setCk);
            Buffer.BlockCopy(scb, 0, buf, primaryOff + 2, 2);

            WriteCluster(cluster, buf);
        }

        private void MarkDirEntryFree(uint dirCluster, int startIndex, int count)
        {
            var buf = ReadCluster(dirCluster);
            for (int i = 0; i < count; i++)
            {
                int off = (startIndex + i) * 32;
                buf[off + 0] = 0x00;
            }
            WriteCluster(dirCluster, buf);
        }

        // Scan root directory for Allocation Bitmap (0x81) and Up-case Table (0x82)
        private void ScanVolumeMetadata()
        {
            List<uint> chain = GetClusterChain(RootDirCluster);
            if (chain.Count == 0)
            {
                chain.Add(RootDirCluster);
            }
            int entrySize = 32;
            for (int ci = 0; ci < chain.Count; ci++)
            {
                var data = ReadCluster(chain[ci]);
                int entries = data.Length / entrySize;
                for (int e = 0; e < entries; e++)
                {
                    int off = e * entrySize;
                    byte type = data[off + 0];
                    if (type == 0x00)
                    {
                        continue;
                    }
                    if ((type & 0x80) == 0)
                    {
                        continue; // not in-use
                    }
                    if (type == 0x81)
                    {
                        // Allocation Bitmap
                        byte id = data[off + 1];
                        uint first = BitConverter.ToUInt32(data, off + 20);
                        ulong len = BitConverter.ToUInt64(data, off + 24);
                        var chainBm = GetClusterChain(first);
                        if (chainBm.Count == 0 && first >= 2)
                        {
                            chainBm.Add(first);
                        }
                        _bitmaps.Add(new BitmapInfo { Identifier = id, FirstCluster = first, DataLength = len, Chain = chainBm });
                    }
                    else if (type == 0x82)
                    {
                        // Up-case Table
                        _upcaseFirstCluster = BitConverter.ToUInt32(data, off + 20);
                        _upcaseLength = BitConverter.ToUInt64(data, off + 24);
                    }
                }
            }
        }

        private BitmapInfo? GetActiveBitmap()
        {
            if (_bitmaps.Count == 0)
            {
                return null;
            }
            // Prefer bitmap whose Identifier matches ActiveFat; else fallback to first
            for (int i = 0; i < _bitmaps.Count; i++)
            {
                if (_bitmaps[i].Identifier == ActiveFat)
                {
                    return _bitmaps[i];
                }
            }
            return _bitmaps[0];
        }

        private void SetAllocationBitmap(uint cluster, bool allocated)
        {
            if (cluster < 2)
            {
                return;
            }
            if (_bitmaps.Count == 0)
            {
                return; // no bitmap known
            }
            // Update all bitmaps we know about to be safe
            for (int i = 0; i < _bitmaps.Count; i++)
            {
                UpdateBitmapOnChain(_bitmaps[i], cluster, allocated);
            }
        }

        private void UpdateBitmapOnChain(BitmapInfo bm, uint cluster, bool allocated)
        {
            if (bm.FirstCluster < 2 || bm.Chain == null || bm.Chain.Count == 0)
            {
                return;
            }
            uint bitIndex = cluster - 2; // bit 0 => cluster 2
            ulong byteIndex = bitIndex / 8u;
            int bitInByte = (int)(bitIndex % 8u); // low-order bit is first
            if ((ulong)bm.DataLength <= byteIndex)
            {
                return; // outside bitmap length
            }
            uint bytesPerCluster = BytesPerCluster;
            int clusterIdx = (int)(byteIndex / bytesPerCluster);
            int offsetInCluster = (int)(byteIndex % bytesPerCluster);
            if (clusterIdx >= bm.Chain.Count)
            {
                return;
            }
            var cl = bm.Chain[clusterIdx];
            var buf = ReadCluster(cl);
            byte b = buf[offsetInCluster];
            byte mask = (byte)(1 << bitInByte);
            if (allocated)
            {
                b = (byte)(b | mask);
            }
            else
            {
                b = (byte)(b & ~mask);
            }
            buf[offsetInCluster] = b;
            WriteCluster(cl, buf);
        }

    internal static ushort ComputeNameHash(string name)
        {
            // exFAT rolling checksum over up-cased UTF-16 name (NameLength * 2 bytes)
            // Here we approximate using basic ASCII upper-case mapping for cross-platform friendliness.
            ushort sum = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (ch >= 'a' && ch <= 'z')
                {
                    ch = (char)(ch - 32);
                }
                ushort u = ch;
                byte lo = (byte)(u & 0xFF);
                byte hi = (byte)((u >> 8) & 0xFF);
                sum = (ushort)(((sum << 15) | (sum >> 1)) + lo);
                sum = (ushort)(((sum << 15) | (sum >> 1)) + hi);
            }
            return sum;
        }

    internal static ushort ComputeEntrySetChecksum(byte[] clusterBuf, int primaryOffset, int totalEntries)
        {
            // 16-bit rolling checksum over the bytes of all directory entries in the set,
            // excluding bytes [2..3] of the primary entry (the checksum field itself).
            int bytesPerEntry = 32;
            ushort sum = 0;
            for (int e = 0; e < totalEntries; e++)
            {
                int off = primaryOffset + (e * bytesPerEntry);
                for (int i = 0; i < bytesPerEntry; i++)
                {
                    if (e == 0 && (i == 2 || i == 3))
                    {
                        continue;
                    }
                    sum = (ushort)(((sum << 15) | (sum >> 1)) + clusterBuf[off + i]);
                }
            }
            return sum;
        }
    }

    internal sealed class ExFatDirectoryEntry : DirectoryEntry
    {
        internal uint FirstCluster;
        internal ulong Length;
        internal ushort Attributes;
        internal uint DirCluster;
        internal uint EntryIndex;
        internal uint SecondaryCount;
        internal int StreamEntryIndex;
        internal int NameEntryStartIndex;
        internal uint NameEntryCount;
        internal ExFatFileSystem FS { get { return (ExFatFileSystem)mFileSystem; } }

        internal ExFatDirectoryEntry(FileSystem aFileSystem, DirectoryEntry aParent, string aFullPath, string aName, long aSize, DirectoryEntryTypeEnum aEntryType)
            : base(aFileSystem, aParent, aFullPath, aName, aSize, aEntryType)
        { }

        public override void SetName(string aName)
        {
            if (string.IsNullOrWhiteSpace(aName))
            {
                throw new ArgumentNullException(nameof(aName));
            }
            ExFatFileSystem fs = (ExFatFileSystem)mFileSystem;
            int needed = (aName.Length + 14) / 15;
            if (needed > NameEntryCount)
            {
                throw new NotImplementedException("Growing name entries not yet supported");
            }
            var buf = fs.ReadCluster(DirCluster);
            if (StreamEntryIndex >= 0)
            {
                int streamOff = StreamEntryIndex * 32;
                buf[streamOff + 3] = (byte)aName.Length;
                // Update NameHash
                ushort nameHash = ExFatFileSystem.ComputeNameHash(aName);
                var nh = BitConverter.GetBytes(nameHash);
                Buffer.BlockCopy(nh, 0, buf, streamOff + 0x04, 2);
            }
            var chars = aName.ToCharArray();
            int processed = 0;
            for (int ne = 0; ne < (int)NameEntryCount; ne++)
            {
                int off = (NameEntryStartIndex + ne) * 32;
                for (int j = 0; j < 15; j++)
                {
                    ushort ch = 0;
                    if (processed < chars.Length)
                    {
                        ch = chars[processed++];
                    }
                    var chb = BitConverter.GetBytes(ch);
                    Buffer.BlockCopy(chb, 0, buf, off + 2 + j * 2, 2);
                }
            }
            // Update Directory Entry Set Checksum
            int primaryOff = (int)EntryIndex * 32;
            ushort setCk = ExFatFileSystem.ComputeEntrySetChecksum(buf, primaryOff, 1 + (int)SecondaryCount);
            var scb = BitConverter.GetBytes(setCk);
            Buffer.BlockCopy(scb, 0, buf, primaryOff + 2, 2);
            fs.WriteCluster(DirCluster, buf);
            mName = aName;
        }

        public override void SetSize(long aSize)
        {
            ExFatFileSystem fs = (ExFatFileSystem)mFileSystem;
            var buf = fs.ReadCluster(DirCluster);
            if (StreamEntryIndex >= 0)
            {
                int streamOff = StreamEntryIndex * 32;
                var len = BitConverter.GetBytes((ulong)aSize);
                Buffer.BlockCopy(len, 0, buf, streamOff + 0x18, 8);
                Buffer.BlockCopy(len, 0, buf, streamOff + 0x08, 8);
            }
            // Update Directory Entry Set Checksum post size change
            int primaryOff = (int)EntryIndex * 32;
            ushort setCk = ExFatFileSystem.ComputeEntrySetChecksum(buf, primaryOff, 1 + (int)SecondaryCount);
            var scb = BitConverter.GetBytes(setCk);
            Buffer.BlockCopy(scb, 0, buf, primaryOff + 2, 2);
            fs.WriteCluster(DirCluster, buf);
            mSize = aSize;
            Length = (ulong)aSize;
        }

        public override Stream GetFileStream()
        {
            if (mEntryType != DirectoryEntryTypeEnum.File)
            {
                return null;
            }
            return new ExFatStream(this);
        }

        public override long GetUsedSpace()
        {
            return mSize;
        }

        // exFAT timestamp helpers
        private static uint ToExFatTimestamp(DateTime dt)
        {
            // exFAT stores seconds since 1 Jan 1980 00:00:00 UTC
            // Avoid TimeZoneInfo to prevent GPFs in Cosmos; treat input as UTC if not already
            DateTime epoch = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime utc = dt.Kind == DateTimeKind.Utc ? dt : new DateTime(dt.Ticks, DateTimeKind.Utc);
            if (utc < epoch)
            {
                utc = epoch;
            }
            var span = utc - epoch;
            if (span.TotalSeconds <= 0)
            {
                return 0;
            }
            if (span.TotalSeconds >= uint.MaxValue)
            {
                return uint.MaxValue;
            }
            return (uint)span.TotalSeconds;
        }

        private byte ToExFatUtcOffsetByte(DateTime dt)
        {
            // Avoid TimeZoneInfo in Cosmos; store zero offset (UTC)
            return 0;
        }

        private void UpdatePrimarySetChecksum(byte[] dirClusterBuf)
        {
            int primaryOff = (int)EntryIndex * 32;
            ushort setCk = ExFatFileSystem.ComputeEntrySetChecksum(dirClusterBuf, primaryOff, 1 + (int)SecondaryCount);
            var scb = BitConverter.GetBytes(setCk);
            Buffer.BlockCopy(scb, 0, dirClusterBuf, primaryOff + 2, 2);
        }

        private static DateTime FromExFatTimestamp(uint seconds, sbyte utcOffsetQuarters)
        {
            // Interpret as seconds since 1980-01-01 UTC and return UTC DateTime.
            // Ignore stored offset to avoid TimeZoneInfo usage in Cosmos.
            DateTime epoch = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return new DateTime(epoch.Ticks + seconds * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }

        public override void SetCreationTime(DateTime creationTime)
        {
            var buf = FS.ReadCluster(DirCluster);
            int primaryOff = (int)EntryIndex * 32;
            // Primary file entry fields:
            // +8  (4) CreateTimestamp
            // +20 (1) Create10msIncrement (0..199; we store from milliseconds)
            // +22 (1) CreateUtcOffset
            uint ts = ToExFatTimestamp(creationTime);
            var tsb = BitConverter.GetBytes(ts);
            Buffer.BlockCopy(tsb, 0, buf, primaryOff + 8, 4);
            // 10ms increments
            int tenMs = (int)((creationTime.Millisecond % 1000) / 10);
            if (tenMs < 0)
            {
                tenMs = 0;
            }
            if (tenMs > 199)
            {
                tenMs = 199;
            }
            buf[primaryOff + 20] = (byte)tenMs;
            // UTC offset
            buf[primaryOff + 22] = ToExFatUtcOffsetByte(creationTime);
            // Recompute checksum
            UpdatePrimarySetChecksum(buf);
            FS.WriteCluster(DirCluster, buf);
        }

        public override void SetLastWriteTime(DateTime lastWriteTime)
        {
            var buf = FS.ReadCluster(DirCluster);
            int primaryOff = (int)EntryIndex * 32;
            // +12 (4) LastModifiedTimestamp
            // +21 (1) LastModified10msIncrement
            // +23 (1) LastModifiedUtcOffset
            uint ts = ToExFatTimestamp(lastWriteTime);
            var tsb = BitConverter.GetBytes(ts);
            Buffer.BlockCopy(tsb, 0, buf, primaryOff + 12, 4);
            int tenMs = (int)((lastWriteTime.Millisecond % 1000) / 10);
            if (tenMs < 0)
            {
                tenMs = 0;
            }
            if (tenMs > 199)
            {
                tenMs = 199;
            }
            buf[primaryOff + 21] = (byte)tenMs;
            buf[primaryOff + 23] = ToExFatUtcOffsetByte(lastWriteTime);
            UpdatePrimarySetChecksum(buf);
            FS.WriteCluster(DirCluster, buf);
        }

        public override void SetLastAccessTime(DateTime lastAccessTime)
        {
            var buf = FS.ReadCluster(DirCluster);
            int primaryOff = (int)EntryIndex * 32;
            // +16 (4) LastAccessedTimestamp
            // +24 (1) LastAccessedUtcOffset
            uint ts = ToExFatTimestamp(lastAccessTime);
            var tsb = BitConverter.GetBytes(ts);
            Buffer.BlockCopy(tsb, 0, buf, primaryOff + 16, 4);
            buf[primaryOff + 24] = ToExFatUtcOffsetByte(lastAccessTime);
            UpdatePrimarySetChecksum(buf);
            FS.WriteCluster(DirCluster, buf);
        }

        public override DateTime GetCreationTime()
        {
            var buf = FS.ReadCluster(DirCluster);
            int primaryOff = (int)EntryIndex * 32;
            uint seconds = BitConverter.ToUInt32(buf, primaryOff + 8);
            sbyte utcQ = unchecked((sbyte)buf[primaryOff + 22]);
            return FromExFatTimestamp(seconds, utcQ);
        }

        public override DateTime GetLastWriteTime()
        {
            var buf = FS.ReadCluster(DirCluster);
            int primaryOff = (int)EntryIndex * 32;
            uint seconds = BitConverter.ToUInt32(buf, primaryOff + 12);
            sbyte utcQ = unchecked((sbyte)buf[primaryOff + 23]);
            return FromExFatTimestamp(seconds, utcQ);
        }

        public override DateTime GetLastAccessTime()
        {
            var buf = FS.ReadCluster(DirCluster);
            int primaryOff = (int)EntryIndex * 32;
            uint seconds = BitConverter.ToUInt32(buf, primaryOff + 16);
            sbyte utcQ = unchecked((sbyte)buf[primaryOff + 24]);
            return FromExFatTimestamp(seconds, utcQ);
        }

        public override FileAttributes GetAttributes()
        {
            // Map exFAT attribute bits (primary entry +4..+5) to System.IO.FileAttributes
            // Only supported bits in exFAT: 0x01 RO, 0x02 Hidden, 0x04 System, 0x10 Directory, 0x20 Archive
            FileAttributes attrs = 0;
            if ((Attributes & 0x01) != 0) { attrs |= FileAttributes.ReadOnly; }
            if ((Attributes & 0x02) != 0) { attrs |= FileAttributes.Hidden; }
            if ((Attributes & 0x04) != 0) { attrs |= FileAttributes.System; }
            if ((Attributes & 0x10) != 0) { attrs |= FileAttributes.Directory; }
            if ((Attributes & 0x20) != 0) { attrs |= FileAttributes.Archive; }
            // Compute Normal flag for files when no other flags are set
            if (attrs == 0 && mEntryType != DirectoryEntryTypeEnum.Directory)
            {
                attrs |= FileAttributes.Normal;
            }
            return attrs;
        }

        public override void SetAttributes(FileAttributes attributes)
        {
            // Sanity rules:
            // - FileAttributes.Normal means no other flags; ignore it if combined with others
            // - Directory flag must match actual entry type
            // - Only map supported exFAT bits: RO(0x01), Hidden(0x02), System(0x04), Directory(0x10), Archive(0x20)
            ushort newAttr = 0;

            bool onlyNormal = (attributes == FileAttributes.Normal);
            if (!onlyNormal)
            {
                if ((attributes & FileAttributes.ReadOnly) != 0) { newAttr |= 0x01; }
                if ((attributes & FileAttributes.Hidden) != 0) { newAttr |= 0x02; }
                if ((attributes & FileAttributes.System) != 0) { newAttr |= 0x04; }
                if ((attributes & FileAttributes.Archive) != 0) { newAttr |= 0x20; }
                // Ignore Device/Normal/Temporary/Compressed etc. for exFAT metadata
            }

            if (mEntryType == DirectoryEntryTypeEnum.Directory)
            {
                // Ensure Directory bit is set for directories
                newAttr |= 0x10;
            }
            else
            {
                // Ensure Directory bit is not set for files
                newAttr = (ushort)(newAttr & ~0x10);
            }

            var buf = FS.ReadCluster(DirCluster);
            int primaryOff = (int)EntryIndex * 32;
            var ab = BitConverter.GetBytes(newAttr);
            Buffer.BlockCopy(ab, 0, buf, primaryOff + 4, 2);
            // Update checksum
            ushort setCk = ExFatFileSystem.ComputeEntrySetChecksum(buf, primaryOff, 1 + (int)SecondaryCount);
            var scb = BitConverter.GetBytes(setCk);
            Buffer.BlockCopy(scb, 0, buf, primaryOff + 2, 2);
            FS.WriteCluster(DirCluster, buf);
            Attributes = newAttr;
        }
    }

    internal sealed class ExFatStream : Stream
    {
        private readonly ExFatDirectoryEntry _entry;
        private readonly ExFatFileSystem _fs;
        private long _pos;

        internal ExFatStream(ExFatDirectoryEntry entry)
        {
            _entry = entry;
            _fs = entry.FS;
            _pos = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _entry.mSize;
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
            if (_entry.FirstCluster < 2 || count == 0 || _pos >= Length)
            {
                return 0;
            }

            var chain = _fs.GetClusterChain(_entry.FirstCluster);
            if (chain.Count == 0)
            {
                chain.Add(_entry.FirstCluster);
            }
            int bytesPerCluster = (int)_fs.BytesPerCluster;
            int totalRead = 0;
            while (count > 0 && _pos < Length)
            {
                int clusterIndex = (int)(_pos / bytesPerCluster);
                int offsetInCluster = (int)(_pos % bytesPerCluster);
                if (clusterIndex >= chain.Count)
                {
                    break;
                }
                var cl = chain[clusterIndex];
                var data = _fs.ReadCluster(cl);
                int toCopy = Math.Min(count, Math.Min(bytesPerCluster - offsetInCluster, (int)(Length - _pos)));
                Buffer.BlockCopy(data, offsetInCluster, buffer, offset, toCopy);
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
                _pos += toCopy;
            }
            if (totalRead > 0)
            {
                _entry.SetLastAccessTime(DateTime.Now);
            }
            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => Length + offset,
                _ => _pos
            };
            if (newPos < 0)
            {
                throw new IOException("Negative seek");
            }
            _pos = newPos;
            return _pos;
        }

        public override void SetLength(long value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            EnsureSize(value);
            _entry.SetLastWriteTime(DateTime.Now);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException();
            }

            long endPos = _pos + count;
            if (endPos > Length)
            {
                EnsureSize(endPos);
            }

            var chain = _fs.GetClusterChain(_entry.FirstCluster);
            if (chain.Count == 0)
            {
                chain.Add(_entry.FirstCluster);
            }
            int bytesPerCluster = (int)_fs.BytesPerCluster;
            int remaining = count;
            while (remaining > 0)
            {
                int clusterIndex = (int)(_pos / bytesPerCluster);
                int offsetInCluster = (int)(_pos % bytesPerCluster);
                var cl = chain[clusterIndex];
                var data = _fs.ReadCluster(cl);
                int toCopy = Math.Min(remaining, bytesPerCluster - offsetInCluster);
                Buffer.BlockCopy(buffer, offset, data, offsetInCluster, toCopy);
                _fs.WriteCluster(cl, data);
                offset += toCopy;
                remaining -= toCopy;
                _pos += toCopy;
            }
            _entry.SetLastWriteTime(DateTime.Now);
        }

        private void EnsureSize(long target)
        {
            int bytesPerCluster = (int)_fs.BytesPerCluster;
            long currentClusters = (_entry.mSize + bytesPerCluster - 1) / bytesPerCluster;
            long neededClusters = (target + bytesPerCluster - 1) / bytesPerCluster;
            if (_entry.FirstCluster < 2 && neededClusters > 0)
            {
                var chain = _fs.AllocateClusterChain(1);
                _entry.FirstCluster = chain[0];
                var buf = _fs.ReadCluster(_entry.DirCluster);
                int streamOff = _entry.StreamEntryIndex * 32;
                var fcl = BitConverter.GetBytes(_entry.FirstCluster);
                Buffer.BlockCopy(fcl, 0, buf, streamOff + 0x14, 4);
                int primaryOff = (int)_entry.EntryIndex * 32;
                ushort setCk = ExFatFileSystem.ComputeEntrySetChecksum(buf, primaryOff, 1 + (int)_entry.SecondaryCount);
                var scb = BitConverter.GetBytes(setCk);
                Buffer.BlockCopy(scb, 0, buf, primaryOff + 2, 2);
                _fs.WriteCluster(_entry.DirCluster, buf);
                currentClusters = 1;
            }
            while (neededClusters > currentClusters)
            {
                var add = _fs.AllocateClusterChain(1);
                var chain = _fs.GetClusterChain(_entry.FirstCluster);
                uint last = chain[chain.Count - 1];
                _fs.SetFatEntry(last, add[0]);
                _fs.SetFatEntry(add[0], 0xFFFFFFFF);
                currentClusters++;
            }
            _entry.mSize = target;
            _entry.SetSize(target);
        }
    }
}
