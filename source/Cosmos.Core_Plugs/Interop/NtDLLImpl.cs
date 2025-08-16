using IL2CPU.API.Attribs;
using System;

namespace Cosmos.Core_Plugs.Interop
{
    [Plug("Interop+NtDll, System.Private.CoreLib", IsOptional = true)]
    public static unsafe class NtDllImpl
    {
        public static uint NtQuerySystemInformation(int a, void* b, uint c, uint* d)
        {
            throw new NotImplementedException();
        }
    }
}
