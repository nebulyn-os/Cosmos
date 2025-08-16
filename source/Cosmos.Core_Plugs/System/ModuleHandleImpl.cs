using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IL2CPU.API.Attribs;

namespace Cosmos.Core_Plugs.System
{
    [Plug(Target = typeof(ModuleHandle))]
    internal class ModuleHandleImpl
    {
        [PlugMethod(Signature = "System_IntPtr__System_ModuleHandle__GetMetadataImport_System_Reflection_RuntimeModule_")]
        public static IntPtr _GetMetadataImport(global::System.Reflection.RuntimeModule rM)
        {
            throw new NotImplementedException("ModuleHandleImpl._GetMetadataImport is not implemented yet.");
        }

        [PlugMethod(Signature = "System_Void__System_ModuleHandle_ResolveType_System_Runtime_CompilerServices_QCallModule__System_Int32__System_IntPtr#__System_Int32__System_IntPtr#__System_Int32__System_Runtime_CompilerServices_ObjectHandleOnStack_")]
        public static unsafe void ResolveType(global::System.Runtime.CompilerServices.QCallModule a, int b, int* c, int d, int* e, int f, global::System.Runtime.CompilerServices.ObjectHandleOnStack g)
        {
            throw new NotImplementedException("ModuleHandleImpl.ResolveType is not implemented yet.");
        }

        [PlugMethod(Signature = "System_RuntimeMethodHandleInternal__System_ModuleHandle_ResolveMethod_System_Runtime_CompilerServices_QCallModule__System_Int32__System_IntPtr#__System_Int32__System_IntPtr#__System_Int32_")]
        public static unsafe global::System.RuntimeMethodHandleInternal ResolveMethod(global::System.Runtime.CompilerServices.QCallModule a, int b, IntPtr* c, int d, IntPtr* e, int f)
        {
            throw new NotImplementedException("ModuleHandleImpl.ResolveMethod is not implemented yet.");
        }
    }
}
