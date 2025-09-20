using System;
using System.Text;
using Cosmos.HAL.BlockDevice;

namespace Cosmos.System.FileSystem.ExFAT
{
    public class ExFatFileSystemFactory : FileSystemFactory
    {
        public override string Name => "exFAT";

        public override bool IsType(Partition aDevice)
        {
            if (aDevice == null)
            {
                throw new ArgumentNullException(nameof(aDevice));
            }
            // Prefer partition metadata first when possible (MBR). exFAT typically uses MBR type 0x07 (Microsoft Basic/NTFS/exFAT)
            // but this is not unique. We use it as a gate to exclude clearly non-Microsoft partitions.
            if (!GPT.IsGPTPartition(aDevice.Host))
            {
                MBR mbr = new MBR(aDevice.Host);
                bool found = false;
                for (int i = 0; i < mbr.Partitions.Count; i++)
                {
                    MBR.PartInfo pi = mbr.Partitions[i];
                    if (pi.StartSector == aDevice.StartingSector && pi.SectorCount == aDevice.BlockCount)
                    {
                        found = true;
                        if (pi.SystemID != 0x07)
                        {
                            return false; // not a Microsoft/exFAT/NTFS type
                        }
                        break;
                    }
                }
                // If not found in MBR, we still proceed to content-based checks.
            }

            byte[] boot = aDevice.NewBlockArray(1);
            aDevice.ReadBlock(0UL, 1U, ref boot);

            // Check exFAT OEM ID
            string oem = Encoding.ASCII.GetString(boot, 3, 8);
            if (oem != "EXFAT   ")
            {
                return false;
            }

            // Validate key BPB fields for sanity instead of relying on the boot signature alone
            // Offsets per exFAT spec
            uint fatOffset = BitConverter.ToUInt32(boot, 0x50);
            uint fatLength = BitConverter.ToUInt32(boot, 0x54);
            uint clusterHeapOffset = BitConverter.ToUInt32(boot, 0x58);
            uint clusterCount = BitConverter.ToUInt32(boot, 0x5C);
            byte bytesPerSectorShift = boot[0x6C];
            byte sectorsPerClusterShift = boot[0x6D];

            if (fatOffset == 0 || fatLength == 0 || clusterHeapOffset == 0 || clusterCount == 0)
            {
                return false;
            }
            if (bytesPerSectorShift < 9 || bytesPerSectorShift > 12)
            {
                return false; // 512..4096 typical
            }
            if (sectorsPerClusterShift > 25)
            {
                return false;
            }

            // Passed sanity and OEM checks → treat as exFAT
            return true;
        }

        public override FileSystem Create(Partition aDevice, string aRootPath, long aSize)
            => new ExFatFileSystem(aDevice, aRootPath, aSize);
    }
}
