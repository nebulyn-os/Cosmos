using System;
using System.Text;
using Cosmos.HAL.BlockDevice;

namespace Cosmos.System.FileSystem.NTFS
{
    public class NtfsFileSystemFactory : FileSystemFactory
    {
        public override string Name => "NTFS";

        public override bool IsType(Partition aDevice)
        {
            if (aDevice == null)
            {
                throw new ArgumentNullException(nameof(aDevice));
            }

            var boot = aDevice.NewBlockArray(1);
            aDevice.ReadBlock(0UL, 1U, ref boot);

            // 0x1FE signature must be 0xAA55
            var sig = BitConverter.ToUInt16(boot, 510);
            if (sig != 0xAA55)
            {
                return false;
            }

            // OEM ID at offset 3, length 8
            var oem = Encoding.ASCII.GetString(boot, 3, 8);
            return oem == "NTFS    ";
        }

        public override FileSystem Create(Partition aDevice, string aRootPath, long aSize)
            => new NtfsFileSystem(aDevice, aRootPath, aSize);
    }
}
