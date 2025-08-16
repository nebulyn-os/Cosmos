#define COSMOSDEBUG
using System;
using Cosmos.Debug.Kernel;
using IL2CPU.API.Attribs;

namespace Cosmos.Core_Asm
{
    [Plug(Target = typeof(Array))]
    public class ArrayImpl
    {
        [PlugMethod(Assembler = typeof(ArrayGetLengthAsm))]
        public static int get_Length(Array aThis)
        {
            throw new NotImplementedException();
        }

        public static void Copy(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length)
        {
            Copy(sourceArray, sourceIndex, destinationArray, destinationIndex, length, false);
        }

        [PlugMethod(Assembler = typeof(ArrayInternalCopyAsm))]
        public static void Copy(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length, bool reliable)
        {
            throw new NotImplementedException();
        }

        [PlugMethod(Assembler = typeof(ArrayClearAsm))]
        public static void Clear(Array array)
        {
            throw new NotImplementedException();
        }

        [PlugMethod()]
        public static unsafe bool IsSimpleCopy(Array sourceArray, Array destinationArray)
        {
            return false;
        }

        public enum ArrayAssignType
        {
            SimpleCopy,
            WrongType,
            MustCast,
            BoxValueClassOrPrimitive,
            UnboxValueClass,
            PrimitiveWiden,
        }

        [PlugMethod()]
        public static void CopySlow(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length, ArrayAssignType assignType)
        {
            throw new NotImplementedException("CopySlow() not Impl'd.");
        }

        [PlugMethod()]
        public static void CopySlow(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length)
        {
            throw new NotImplementedException("CopySlow() not Impl'd.");
        }
    }
}
