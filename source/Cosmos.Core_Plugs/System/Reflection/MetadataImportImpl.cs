using System;
using IL2CPU.API.Attribs;

namespace Cosmos.Core_Plugs.System.Reflection
{
    [Plug("System.Reflection.MetadataImport, System.Private.CoreLib")]

    class MetadataImportImpl
    {
        [PlugMethod(Signature = "System_Void__System_Reflection_MetadataImport__GetGenericParamProps_System_IntPtr__System_Int32___System_Int32_")]
        public static unsafe void __GetGenericParamProps(IntPtr aPtr1, int aInt, int* aPtr2)
        {
            throw new NotImplementedException();
        }

        public static void _GetParentToken(IntPtr aIntPtr, int aInt, ref int aInt1)
        {
            throw new NotImplementedException();
        }

        [PlugMethod(Signature = "System_Void__System_Reflection_MetadataImport__GetCustomAttributeProps_System_IntPtr__System_Int32___System_Int32___System_Reflection_ConstArray_")]
        public static void _GetCustomAttributeProps(IntPtr pCA, int tkCA, out int ptkObj, out global::System.Reflection.ConstArray pVal)
        {
            throw new NotImplementedException();
        }

        [PlugMethod(Signature = "System_Void__System_Reflection_MetadataImport__Enum_System_IntPtr__System_Int32__System_Int32___System_Reflection_MetadataEnumResult_")]
        public static void _Enum(IntPtr a, int b, int c, out global::System.Reflection.MetadataEnumResult d)
        {
            throw new NotImplementedException("MetadataImportImpl._Enum is not implemented yet.");
        }

        [PlugMethod(Signature = "System_Void__System_Reflection_MetadataImport__GetMemberRefProps_System_IntPtr__System_Int32___System_Reflection_ConstArray_")]
        public static void _GetMemberRefProps(IntPtr a, int b, out global::System.Reflection.ConstArray c)
        {
            throw new NotImplementedException("MetadataImportImpl._GetMemberRefProps is not implemented yet.");
        }

        [PlugMethod(Signature = "System_Void__System_Reflection_MetadataImport__GetSigOfMethodDef_System_IntPtr__System_Int32___System_Reflection_ConstArray_")]
        public static void _GetSigOfMethodDef(IntPtr a, int b, out global::System.Reflection.ConstArray c)
        {
            throw new NotImplementedException("MetadataImportImpl._GetSigOfMethodDef is not implemented yet.");
        }
    }
}
