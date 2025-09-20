//#define COSMOSDEBUG

using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Net.Mime;
using System.Text;
using Cosmos.HAL.BlockDevice;
using Cosmos.System.FileSystem.FAT;
using Cosmos.System.FileSystem.ISO9660;
using Cosmos.System.FileSystem.VFS;

namespace Cosmos.System.FileSystem
{
    public class Disk
    {
        private readonly List<ManagedPartition> parts = new();

        public bool IsMBR => !GPT.IsGPTPartition(Host);
        /// <summary>
        /// The size of the disk in bytes.
        /// </summary>
        public long Size { get; }
        /// <summary>
        /// List of partitions
        /// </summary>
        public List<ManagedPartition> Partitions
        {
            get
            {
                List<ManagedPartition> converted = new();

                if (Host.Type == BlockDeviceType.RemovableCD)
                {
                    ManagedPartition part = new(new Partition(Host, 0, 1000000000), nameof(ISO9660FileSystemFactory)); // For some reason, BlockCount is always 0, so just put a large value here.

                    if (mountedPartitions[0] != null)
                    {
                        var data = mountedPartitions[0];
                        part.RootPath = data.RootPath;
                        part.MountedFS = data;
                    }

                    converted.Add(part);
                    return converted;
                }

                if (GPT.IsGPTPartition(Host))
                {
                    GPT gpt = new(Host);
                    int i = 0;
                    foreach (var item in gpt.Partitions)
                    {
                        ManagedPartition part = new(new Partition(Host, item.StartSector, item.SectorCount));
                        if (mountedPartitions[i] != null)
                        {
                            var data = mountedPartitions[i];
                            part.RootPath = data.RootPath;
                            part.MountedFS = data;
                        }
                        converted.Add(part);
                        i++;
                    }
                }
                else
                {
                    MBR mbr = new(Host);
                    int i = 0;
                    foreach (var item in mbr.Partitions)
                    {
                        ManagedPartition part = new(new Partition(Host, item.StartSector, item.SectorCount));
                        if (mountedPartitions[i] != null)
                        {
                            var data = mountedPartitions[i];
                            part.RootPath = data.RootPath;
                            part.MountedFS = data;
                        }
                        converted.Add(part);
                        i++;
                    }
                }

                return converted;
            }
        }

        /// <summary>
        /// Main blockdevice that has all of the partitions.
        /// </summary>
        public BlockDevice Host;
        public BlockDeviceType Type => Host.Type;
        public Disk(BlockDevice mainBlockDevice)
        {
            Host = mainBlockDevice;
            foreach (var part in Partition.Partitions)
            {
                if (part.Host == mainBlockDevice)
                {
                    parts.Add(new ManagedPartition(part));
                }
            }
            Size = (long)(mainBlockDevice.BlockCount * mainBlockDevice.BlockSize);
        }
        /// <summary>
        /// Mounts all of the partitions in the disk
        /// </summary>
        public void Mount()
        {
            for (int i = 0; i < Partitions.Count; i++)
            {
                MountPartition(i);
            }
        }
        /// <summary>
        /// Display information about the disk.
        /// </summary>
        public void DisplayInformation()
        {
            if (Partitions.Count > 0)
            {
                for (int i = 0; i < Partitions.Count; i++)
                {
                    Global.Debugger.SendInternal("Partition #: ");
                    Global.Debugger.SendInternal(i + 1);
                    global::System.Console.WriteLine("Partition #: " + (i + 1));
                    Global.Debugger.SendInternal("Block Size:");
                    Global.Debugger.SendInternal(Partitions[i].Host.BlockSize);
                    global::System.Console.WriteLine("Block Size: " + Partitions[i].Host.BlockSize + " bytes");
                    Global.Debugger.SendInternal("Block Count:");
                    Global.Debugger.SendInternal(Partitions[i].Host.BlockCount);
                    global::System.Console.WriteLine("Block Count: " + Partitions[i].Host.BlockCount);
                    Global.Debugger.SendInternal("Size:");
                    Global.Debugger.SendInternal(Partitions[i].Host.BlockCount * Partitions[i].Host.BlockSize / 1024 / 1024);
                    var rawSizeMB = (long)(Partitions[i].Host.BlockCount * Partitions[i].Host.BlockSize / 1024 / 1024);
                    global::System.Console.WriteLine("Size: " + rawSizeMB + " MB");

                    // If mounted, also show the filesystem-reported size for comparison
                    if (mountedPartitions[i] != null)
                    {
                        var fsSizeMB = mountedPartitions[i].Size; // already in MB
                        global::System.Console.WriteLine("Mounted FS Size: " + fsSizeMB + " MB");
                        if (fsSizeMB != rawSizeMB)
                        {
                            global::System.Console.WriteLine("Note: FS size differs from raw partition size by " + (fsSizeMB - rawSizeMB) + " MB");
                        }
                    }
                }
            }
            else
            {
                global::System.Console.WriteLine("No partitions found!");
            }
        }

        /// <summary>
        /// Create a primary MBR partition of the given size in MB.
        /// Places it at the next available aligned LBA and writes entry into the first free slot.
        /// </summary>
        /// <param name="size">Size in MB (must be > 0).</param>
        public void CreatePartition(int size)
        {
            if (size <= 0)
            {
                throw new ArgumentException("size must be > 0", nameof(size));
            }
            if (GPT.IsGPTPartition(Host))
            {
                throw new Exception("Creating partitions with GPT style not yet supported!");
            }

            // Read MBR sector
            byte[] mbrData = Host.NewBlockArray(1);
            Host.ReadBlock(0, 1, ref mbrData);

            // Ensure MBR signature exists; if empty, initialize a fresh MBR
            if (!(mbrData[510] == 0x55 && mbrData[511] == 0xAA))
            {
                new MBR(Host).CreateMBR(Host);
                Host.ReadBlock(0, 1, ref mbrData);
            }

            // Find first free partition slot (0..3)
            int freeSlot = -1;
            for (int slot = 0; slot < 4; slot++)
            {
                int entry = 446 + (slot * 16);
                byte systemId = mbrData[entry + 4];
                if (systemId == 0)
                {
                    freeSlot = slot;
                    break;
                }
            }
            if (freeSlot == -1)
            {
                throw new NotImplementedException("No free primary partition slots. Extended partitions are not yet supported.");
            }

            // Determine next available aligned start LBA
            const ulong alignSectors = 2048; // 1 MiB alignment
            ulong blockSize = Host.BlockSize;
            ulong diskSectors = Host.BlockCount;
            ulong sectorsNeeded = (ulong)((size * 1024L * 1024L + (long)blockSize - 1) / (long)blockSize);

            // Collect existing partitions and compute end of last one
            var mbr = new MBR(Host);
            ulong lastEnd = alignSectors; // start with minimum alignment
            foreach (var p in mbr.Partitions)
            {
                ulong end = p.StartSector + p.SectorCount; // first free after this part
                if (end > lastEnd)
                {
                    lastEnd = end;
                }
            }
            // Align start
            ulong startLBA = ((lastEnd + alignSectors - 1) / alignSectors) * alignSectors;
            if (startLBA < alignSectors)
            {
                startLBA = alignSectors;
            }
            if (startLBA + sectorsNeeded > diskSectors)
            {
                throw new ArgumentException("Not enough free space for requested partition size.");
            }

            // Create partition object and write MBR entry via helper
            var partition = new Partition(Host, startLBA, sectorsNeeded);
            new MBR(Host).WritePartitionInformation(partition, (byte)freeSlot);

            // Track in Partition registry and local list for legacy consumers
            Partition.Partitions.Add(partition);
            parts.Add(new ManagedPartition(partition));
        }
        /// <summary>
        /// Deletes a partition
        /// </summary>
        /// <param name="index">Partition index starting from 0</param>
        public void DeletePartition(int index)
        {
            if (GPT.IsGPTPartition(Host))
            {
                throw new Exception("Deleting partitions with GPT style not yet supported!");
            }
            var location = 446 + 16 * index;

            byte[] mbr = Host.NewBlockArray(1);
            Host.ReadBlock(0, 1, ref mbr);
            for (int i = location; i < location + 16; i++)
            {
                mbr[i] = 0;
            }
            Host.WriteBlock(0, 1, ref mbr);

            var part = parts[index];
            Partition.Partitions.Remove(part.Host);
            parts.RemoveAt(index);
        }
        /// <summary>
        /// Deletes all partitions on the disk.
        /// </summary>
        public void Clear()
        {
            if (GPT.IsGPTPartition(Host))
            {
                throw new Exception("Removing all partitions with GPT style not yet supported!");
            }
            // Repeatedly delete first partition until none remain to avoid index shifting issues
            while (Partitions.Count > 0)
            {
                DeletePartition(0);
            }
        }

        public void FormatPartition(int index, string format, bool quick = true)
        {
            var part = Partitions[index];

            // Use the selected partition's size, not the whole disk size
            var xSize = (long)(part.Host.BlockCount * part.Host.BlockSize / 1024 / 1024);

            if (format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))
            {
                FatFileSystem.CreateFatFileSystem(part.Host, VFSManager.GetNextFilesystemLetter() + ":\\", xSize, format);
                // Force remount of this partition so detection runs again
                if (index >= 0 && index < mountedPartitions.Length)
                {
                    mountedPartitions[index] = null;
                }
                Mount();
            }
            else if (string.Equals(format, "exFAT", StringComparison.OrdinalIgnoreCase))
            {
                // Create and mount exFAT
                var root = VFSManager.GetNextFilesystemLetter() + ":\\";
                global::System.Console.WriteLine($"Formatting Partition #" + (index + 1) + " as exFAT (" + xSize + " MB)...");
                var fs = ExFAT.ExFatFileSystem.CreateExFatFileSystem(part.Host, root, xSize);
                // If MBR, ensure partition type is Microsoft Basic (0x07) for exFAT
                if (!GPT.IsGPTPartition(Host))
                {
                    try
                    {
                        SetMbrPartitionType(index, 0x07);
                    }
                    catch { }
                }
                // Force remount of this partition so detection runs again
                if (index >= 0 && index < mountedPartitions.Length)
                {
                    mountedPartitions[index] = null;
                }
                global::System.Console.WriteLine("Format complete. Mounting partition...");
                MountPartition(index);
                if (mountedPartitions[index] != null)
                {
                    global::System.Console.WriteLine("Mounted exFAT at " + mountedPartitions[index].RootPath + " (" + mountedPartitions[index].Size + " MB)");
                }
                else
                {
                    global::System.Console.WriteLine("Mount failed: file system not detected on partition #" + (index + 1));
                }
            }
            else
            {
                throw new NotImplementedException(format + " formatting not supported.");
            }
        }

        private void SetMbrPartitionType(int index, byte type)
        {
            if (GPT.IsGPTPartition(Host))
            {
                return;
            }
            byte[] mbr = Host.NewBlockArray(1);
            Host.ReadBlock(0, 1, ref mbr);
            int entry = 446 + (index * 16);
            mbr[entry + 4] = type;
            Host.WriteBlock(0, 1, ref mbr);
        }

        private readonly FileSystem[] mountedPartitions = new FileSystem[4];

        /// <summary>
        /// Mounts a partition
        /// </summary>
        /// <param name="index">Partiton index</param>
        public void MountPartition(int index)
        {
            var part = Partitions[index];
            //Don't remount
            if (mountedPartitions[index] != null)
            {
                //We already mounted this partiton
                return;
            }
            string xRootPath = string.Concat(VFSManager.GetNextFilesystemLetter(), VFSBase.VolumeSeparatorChar, VFSBase.DirectorySeparatorChar);
            // Use the partition's size for filesystem creation/mount decisions
            var xSize = (long)(part.Host.BlockCount * part.Host.BlockSize / 1024 / 1024);

            foreach (var item in FileSystemManager.RegisteredFileSystems)
            {
                if (part.LimitFS != null && item.GetType().Name != part.LimitFS)
                {
                    Kernel.PrintDebug("Did not mount partition " + index + " as " + item.GetType().Name + " because the partition has been limited to being a " + part.LimitFS);
                    continue;
                }

                if (item.IsType(part.Host))
                {
                    Kernel.PrintDebug("Mounted partition.");

                    //We would have done Partitions[i].MountedFS = item.Create(...), but since the array is not cached, we need to store the mounted partitions in a list
                    mountedPartitions[index] = item.Create(part.Host, xRootPath, xSize);
                    return;
                }
            }
            Kernel.PrintDebug("Cannot find file system for partiton.");
        }
    }
}
