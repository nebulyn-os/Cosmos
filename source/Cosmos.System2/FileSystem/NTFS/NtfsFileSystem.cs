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
        private readonly string _label;

        internal NtfsFileSystem(Partition aDevice, string aRootPath, long aSize)
            : base(aDevice, aRootPath, aSize)
        {
            if (string.IsNullOrEmpty(aRootPath))
            {
                throw new ArgumentException("Root path required", nameof(aRootPath));
            }

            // Parse minimal NTFS BPB/boot sector
            var boot = aDevice.NewBlockArray(1);
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
            // MFT Logical Cluster Number at 0x30 (8 bytes, little endian signed, but treat as ulong)
            MFTCluster = BitConverter.ToUInt64(boot, 0x30);

            // NTFS volume label is stored in $Volume file; we cannot parse it now; default to Name
            _label = "NTFS";
        }

        public override void DisplayFileSystemInfo()
        {
            global::System.Console.WriteLine($"NTFS: {RootPath} bytes/sector={BytesPerSector}, sectors/cluster={SectorsPerCluster}, MFT LCN={MFTCluster}");
        }

        public override List<DirectoryEntry> GetDirectoryListing(DirectoryEntry baseDirectory)
        {
            // Minimal stub: NTFS parsing is complex. We return empty for now.
            return new List<DirectoryEntry>();
        }

        public override DirectoryEntry GetRootDirectory()
        {
            return new NtfsDirectoryEntry(this, null, RootPath, RootPath.TrimEnd('\\', '/'), 0, DirectoryEntryTypeEnum.Directory);
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
    }

    internal sealed class NtfsDirectoryEntry : DirectoryEntry
    {
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
            throw new NotImplementedException("NTFS file read not implemented yet");
        }

        public override long GetUsedSpace()
        {
            return mSize;
        }
    }
}
