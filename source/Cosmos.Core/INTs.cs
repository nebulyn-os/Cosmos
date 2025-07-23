using System.Runtime.InteropServices;

using IL2CPU.API.Attribs;

namespace Cosmos.Core {
    /// <summary>
    /// INTs (INTerruptS) class.
    /// </summary>
    public class INTs {
        #region Enums
        // TODO: Protect IRQs like memory and ports are
        // TODO: Make IRQs so they are not hookable, and instead release high priority threads like FreeBSD (When we get threading)
        /// <summary>
        /// EFlags Enum.
        /// </summary>
        public enum EFlagsEnum : uint {
            /// <summary>
            /// Set by arithmetic instructions, can be carry or borrow.
            /// </summary>
            Carry = 1,
            /// <summary>
            ///  Set by most CPU instructions if the LSB of the destination operand contain an even number of 1's.
            /// </summary>
            Parity = 1 << 2,
            /// <summary>
            /// Set when an arithmetic carry or borrow has been generated out of the four LSBs.
            /// </summary>
            AuxilliaryCarry = 1 << 4,
            /// <summary>
            /// Set to 1 if an arithmetic result is zero, and reset otherwise.
            /// </summary>
            Zero = 1 << 6,
            /// <summary>
            /// Set to 1 if the last arithmetic result was positive, and reset otherwise.
            /// </summary>
            Sign = 1 << 7,
            /// <summary>
            /// When set to 1, permits single step operations.
            /// </summary>
            Trap = 1 << 8,
            /// <summary>
            /// When set to 1, maskable hardware interrupts will be handled, and ignored otherwise.
            /// </summary>
            InterruptEnable = 1 << 9,
            /// <summary>
            /// When set to 1, strings is processed from highest address to lowest, and from lowest to highest otherwise.
            /// </summary>
            Direction = 1 << 10,
            /// <summary>
            /// Set to 1 if arithmetic overflow has occurred in the last operation.
            /// </summary>
            Overflow = 1 << 11,
            /// <summary>
            /// Set to 1 when one system task invoke another by CALL instruction.
            /// </summary>
            NestedTag = 1 << 14,
            /// <summary>
            /// When set to 1, enables the option turn off certain exceptions while debugging.
            /// </summary>
            Resume = 1 << 16,
            /// <summary>
            /// When set to 1, Virtual8086Mode is enabled.
            /// </summary>
            Virtual8086Mode = 1 << 17,
            /// <summary>
            /// When set to 1, enables alignment check.
            /// </summary>
            AlignmentCheck = 1 << 18,
            /// <summary>
            /// When set, the program will receive hardware interrupts.
            /// </summary>
            VirtualInterrupt = 1 << 19,
            /// <summary>
            /// When set, indicate that there is deferred interrupt pending.
            /// </summary>
            VirtualInterruptPending = 1 << 20,
            /// <summary>
            /// When set, indicate that CPUID instruction is available.
            /// </summary>
            ID = 1 << 21
        }

        /// <summary>
        /// TSS (Task State Segment) struct.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x68)]
        public struct TSS {
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(0)]
            public ushort Link;
            [FieldOffset(4)]
            public uint ESP0;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(8)]
            public ushort SS0;
            [FieldOffset(12)]
            public uint ESP1;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(16)]
            public ushort SS1;
            [FieldOffset(20)]
            public uint ESP2;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(24)]
            public ushort SS2;
            [FieldOffset(28)]
            public uint CR3;
            [FieldOffset(32)]
            public uint EIP;
            [FieldOffset(36)]
            public EFlagsEnum EFlags;
            [FieldOffset(40)]
            public uint EAX;
            [FieldOffset(44)]
            public uint ECX;
            [FieldOffset(48)]
            public uint EDX;
            [FieldOffset(52)]
            public uint EBX;
            [FieldOffset(56)]
            public uint ESP;
            [FieldOffset(60)]
            public uint EBP;
            [FieldOffset(64)]
            public uint ESI;
            [FieldOffset(68)]
            public uint EDI;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(72)]
            public ushort ES;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(76)]
            public ushort CS;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(80)]
            public ushort SS;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(84)]
            public ushort DS;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(88)]
            public ushort FS;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(92)]
            public ushort GS;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(96)]
            public ushort LDTR;
            /// <summary>
            /// Reserved.
            /// </summary>
            [FieldOffset(102)]
            public ushort IOPBOffset;
        }

        [StructLayout(LayoutKind.Explicit, Size = 512)]
        public struct MMXContext {
        }

        [StructLayout(LayoutKind.Explicit, Size = 80)]
        public struct IRQContext {
            [FieldOffset(0)]
            public unsafe MMXContext* MMXContext;

            [FieldOffset(4)]
            public uint EDI;

            [FieldOffset(8)]
            public uint ESI;

            [FieldOffset(12)]
            public uint EBP;

            [FieldOffset(16)]
            public uint ESP;

            [FieldOffset(20)]
            public uint EBX;

            [FieldOffset(24)]
            public uint EDX;

            [FieldOffset(28)]
            public uint ECX;

            [FieldOffset(32)]
            public uint EAX;

            [FieldOffset(36)]
            public uint Interrupt;

            [FieldOffset(40)]
            public uint Param;

            [FieldOffset(44)]
            public uint EIP;

            [FieldOffset(48)]
            public uint CS;

            [FieldOffset(52)]
            public EFlagsEnum EFlags;

            [FieldOffset(56)]
            public uint UserESP;
        }
        #endregion

        /// <summary>
        /// Last known address.
        /// </summary>
        [AsmMarker(AsmMarker.Type.Int_LastKnownAddress)]
        private static uint mLastKnownAddress = 0;

        /// <summary>
        /// IRQ handlers.
        /// </summary>
        private static IRQDelegate[] mIRQ_Handlers = new IRQDelegate[256];

        /// <summary>
        /// Masks or Un-Masks an interupt address.
        /// Source: https://wiki.osdev.org/8259_PIC
        /// </summary>
        /// <param name="aIRQLine">Interupt to unmask.</param>
        /// <param name="aDoMask">True = Mask, False = Unmask.</param>
        public static void SetIRQMaskState(byte aIRQLine, bool aDoMask)
        {
            ushort Port = (ushort)(aIRQLine < 8 ? 0x21 : 0xA1);

            if (aIRQLine >= 8)
            {
                aIRQLine -= 8;
            }

            if (aDoMask)
            {
                IOPort.Write8(Port, (byte)(IOPort.Read8(Port) | (1 << aIRQLine)));
            }
            else
            {
                IOPort.Write8(Port, (byte)(IOPort.Read8(Port) & ~(1 << aIRQLine)));
            }
        }

        // We used to use:
        //Interrupts.IRQ01 += HandleKeyboardInterrupt;
        // But at one point we had issues with multi cast delegates, so we changed to this single cast option.
        // [1:48:37 PM] Matthijs ter Woord: the issues were: "they didn't work, would crash kernel". not sure if we still have them..
        /// <summary>
        /// Set interrupt handler.
        /// </summary>
        /// <param name="aIntNo">Interrupt index.</param>
        /// <param name="aHandler">IRQ handler.</param>
        public static void SetIntHandler(byte aIntNo, IRQDelegate aHandler) {
            mIRQ_Handlers[aIntNo] = aHandler;
        }

        /// <summary>
        /// Set IRQ handler.
        /// </summary>
        /// <param name="aIrqNo">IRQ index.</param>
        /// <param name="aHandler">IRQ handler.</param>
        public static void SetIrqHandler(byte aIrqNo, IRQDelegate aHandler) {
            SetIntHandler((byte)(0x20 + aIrqNo), aHandler);
        }

        /// <summary>
        /// Set IRQ context to IRQ handler.
        /// </summary>
        /// <param name="irq">IRQ handler index.</param>
        /// <param name="aContext">IRQ context.</param>
        private static void IRQ(uint irq, ref IRQContext aContext) {
            var xCallback = mIRQ_Handlers[irq];
            if (xCallback != null) {
                xCallback(ref aContext);
            }
        }

        /// <summary>
        /// Handle default interrupt.
        /// </summary>
        /// <param name="aContext">A IEQ context.</param>
        public static void HandleInterrupt_Default(ref IRQContext aContext) {
            if (aContext.Interrupt >= 0x20 && aContext.Interrupt <= 0x2F) {
                if (aContext.Interrupt >= 0x28) {
                    Global.PIC.EoiSlave();
                } else {
                    Global.PIC.EoiMaster();
                }
            }
        }

        /// <summary>
        /// IRQ delegate.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public delegate void IRQDelegate(ref IRQContext aContext);
        /// <summary>
        /// Exception interrupt delegate.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <param name="aHandled">True if handled.</param>
        public delegate void ExceptionInterruptDelegate(ref IRQContext aContext, ref bool aHandled);

        #region Default Interrupt Handlers

        /// <summary>
        /// IRQ 0 - System timer. Reserved for the system. Cannot be changed by a user.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public static void HandleInterrupt_20(ref IRQContext aContext) {
            IRQ(0x20, ref aContext);
            Global.PIC.EoiMaster();
        }

        //public static IRQDelegate IRQ01;
        /// <summary>
        /// IRQ 1 - Keyboard. Reserved for the system. Cannot be altered even if no keyboard is present or needed.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public static void HandleInterrupt_21(ref IRQContext aContext) {

            IRQ(0x21, ref aContext);
            Global.PIC.EoiMaster();
        }

        public static void HandleInterrupt_22(ref IRQContext aContext) {

            IRQ(0x22, ref aContext);
            Global.PIC.EoiMaster();
        }
        public static void HandleInterrupt_23(ref IRQContext aContext) {

            IRQ(0x23, ref aContext);
            Global.PIC.EoiMaster();
        }
        public static void HandleInterrupt_24(ref IRQContext aContext) {

            IRQ(0x24, ref aContext);
            Global.PIC.EoiMaster();
        }
        public static void HandleInterrupt_25(ref IRQContext aContext) {
            IRQ(0x25, ref aContext);
            Global.PIC.EoiMaster();
        }
        public static void HandleInterrupt_26(ref IRQContext aContext) {

            IRQ(0x26, ref aContext);
            Global.PIC.EoiMaster();
        }
        public static void HandleInterrupt_27(ref IRQContext aContext) {

            IRQ(0x27, ref aContext);
            Global.PIC.EoiMaster();
        }

        public static void HandleInterrupt_28(ref IRQContext aContext) {

            IRQ(0x28, ref aContext);
            Global.PIC.EoiSlave();
        }

        /// <summary>
        /// IRQ 09 - (Added for AMD PCNet network card).
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public static void HandleInterrupt_29(ref IRQContext aContext) {
            IRQ(0x29, ref aContext);
            Global.PIC.EoiSlave();
        }

        /// <summary>
        /// IRQ 10 - (Added for VIA Rhine network card).
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public static void HandleInterrupt_2A(ref IRQContext aContext) {
            IRQ(0x2A, ref aContext);
            Global.PIC.EoiSlave();
        }

        /// <summary>
        /// IRQ 11 - (Added for RTL8139 network card).
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public static void HandleInterrupt_2B(ref IRQContext aContext) {
            IRQ(0x2B, ref aContext);
            Global.PIC.EoiSlave();
        }

        public static void HandleInterrupt_2C(ref IRQContext aContext) {

            IRQ(0x2C, ref aContext);
            Global.PIC.EoiSlave();
        }


        public static void HandleInterrupt_2D(ref IRQContext aContext) {
            IRQ(0x2D, ref aContext);
            Global.PIC.EoiSlave();
        }

        /// <summary>
        /// IRQ 14 - Primary IDE. If no Primary IDE this can be changed.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public static void HandleInterrupt_2E(ref IRQContext aContext) {
            IRQ(0x2E, ref aContext);
            Global.PIC.EoiSlave();
        }

        /// <summary>
        /// IRQ 15 - Secondary IDE.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        public static void HandleInterrupt_2F(ref IRQContext aContext) {
            IRQ(0x2F, ref aContext);
            Global.PIC.EoiSlave();
        }

        public static event IRQDelegate Interrupt30;
        // Interrupt 0x30, enter VMM
        public static void HandleInterrupt_30(ref IRQContext aContext) {
            if (Interrupt30 != null) {
                Interrupt30(ref aContext);
            }
        }

        public static void HandleInterrupt_31(ref IRQContext aContext)
        {
            IRQ(0x31, ref aContext);
        }

        public static void HandleInterrupt_32(ref IRQContext aContext)
        {
            IRQ(0x32, ref aContext);
        }

        public static void HandleInterrupt_33(ref IRQContext aContext)
        {
            IRQ(0x33, ref aContext);
        }

        public static void HandleInterrupt_34(ref IRQContext aContext)
        {
            IRQ(0x34, ref aContext);
        }

        public static void HandleInterrupt_35(ref IRQContext aContext) {
            aContext.EAX *= 2;
            aContext.EBX *= 2;
            aContext.ECX *= 2;
            aContext.EDX *= 2;
        }

        public static void HandleInterrupt_36(ref IRQContext aContext)
        {
            IRQ(0x36, ref aContext);
        }

        public static void HandleInterrupt_37(ref IRQContext aContext)
        {
            IRQ(0x37, ref aContext);
        }

        public static void HandleInterrupt_38(ref IRQContext aContext)
        {
            IRQ(0x38, ref aContext);
        }

        public static void HandleInterrupt_39(ref IRQContext aContext)
        {
            IRQ(0x39, ref aContext);
        }

        public static void HandleInterrupt_3A(ref IRQContext aContext)
        {
            IRQ(0x3A, ref aContext);
        }

        public static void HandleInterrupt_3B(ref IRQContext aContext)
        {
            IRQ(0x3B, ref aContext);
        }

        public static void HandleInterrupt_3C(ref IRQContext aContext)
        {
            IRQ(0x3C, ref aContext);
        }

        public static void HandleInterrupt_3D(ref IRQContext aContext)
        {
            IRQ(0x3D, ref aContext);
        }

        public static void HandleInterrupt_3E(ref IRQContext aContext)
        {
            IRQ(0x3E, ref aContext);
        }

        public static void HandleInterrupt_3F(ref IRQContext aContext)
        {
            IRQ(0x3F, ref aContext);
        }

        public static void HandleInterrupt_40(ref IRQContext aContext) {
            IRQ(0x40, ref aContext);
        }
        public static void HandleInterrupt_41(ref IRQContext aContext) {
            IRQ(0x41, ref aContext);
        }
        public static void HandleInterrupt_42(ref IRQContext aContext) {
            IRQ(0x42, ref aContext);
        }
        public static void HandleInterrupt_43(ref IRQContext aContext) {
            IRQ(0x43, ref aContext);
        }
        public static void HandleInterrupt_44(ref IRQContext aContext) {
            IRQ(0x44, ref aContext);
        }
        public static void HandleInterrupt_45(ref IRQContext aContext) {
            IRQ(0x45, ref aContext);
        }
        public static void HandleInterrupt_46(ref IRQContext aContext) {
            IRQ(0x46, ref aContext);
        }
        public static void HandleInterrupt_47(ref IRQContext aContext) {
            IRQ(0x47, ref aContext);
        }
        public static void HandleInterrupt_48(ref IRQContext aContext) {
            IRQ(0x48, ref aContext);
        }
        public static void HandleInterrupt_49(ref IRQContext aContext) {
            IRQ(0x49, ref aContext);
        }

        public static void HandleInterrupt_4A(ref IRQContext aContext) { IRQ(0x4A, ref aContext); }
        public static void HandleInterrupt_4B(ref IRQContext aContext) { IRQ(0x4B, ref aContext); }
        public static void HandleInterrupt_4C(ref IRQContext aContext) { IRQ(0x4C, ref aContext); }
        public static void HandleInterrupt_4D(ref IRQContext aContext) { IRQ(0x4D, ref aContext); }
        public static void HandleInterrupt_4E(ref IRQContext aContext) { IRQ(0x4E, ref aContext); }
        public static void HandleInterrupt_4F(ref IRQContext aContext) { IRQ(0x4F, ref aContext); }
        public static void HandleInterrupt_50(ref IRQContext aContext) { IRQ(0x50, ref aContext); }
        public static void HandleInterrupt_51(ref IRQContext aContext) { IRQ(0x51, ref aContext); }
        public static void HandleInterrupt_52(ref IRQContext aContext) { IRQ(0x52, ref aContext); }
        public static void HandleInterrupt_53(ref IRQContext aContext) { IRQ(0x53, ref aContext); }
        public static void HandleInterrupt_54(ref IRQContext aContext) { IRQ(0x54, ref aContext); }
        public static void HandleInterrupt_55(ref IRQContext aContext) { IRQ(0x55, ref aContext); }
        public static void HandleInterrupt_56(ref IRQContext aContext) { IRQ(0x56, ref aContext); }
        public static void HandleInterrupt_57(ref IRQContext aContext) { IRQ(0x57, ref aContext); }
        public static void HandleInterrupt_58(ref IRQContext aContext) { IRQ(0x58, ref aContext); }
        public static void HandleInterrupt_59(ref IRQContext aContext) { IRQ(0x59, ref aContext); }
        public static void HandleInterrupt_5A(ref IRQContext aContext) { IRQ(0x5A, ref aContext); }
        public static void HandleInterrupt_5B(ref IRQContext aContext) { IRQ(0x5B, ref aContext); }
        public static void HandleInterrupt_5C(ref IRQContext aContext) { IRQ(0x5C, ref aContext); }
        public static void HandleInterrupt_5D(ref IRQContext aContext) { IRQ(0x5D, ref aContext); }
        public static void HandleInterrupt_5E(ref IRQContext aContext) { IRQ(0x5E, ref aContext); }
        public static void HandleInterrupt_5F(ref IRQContext aContext) { IRQ(0x5F, ref aContext); }
        public static void HandleInterrupt_60(ref IRQContext aContext) { IRQ(0x60, ref aContext); }
        public static void HandleInterrupt_61(ref IRQContext aContext) { IRQ(0x61, ref aContext); }
        public static void HandleInterrupt_62(ref IRQContext aContext) { IRQ(0x62, ref aContext); }
        public static void HandleInterrupt_63(ref IRQContext aContext) { IRQ(0x63, ref aContext); }
        public static void HandleInterrupt_64(ref IRQContext aContext) { IRQ(0x64, ref aContext); }
        public static void HandleInterrupt_65(ref IRQContext aContext) { IRQ(0x65, ref aContext); }
        public static void HandleInterrupt_66(ref IRQContext aContext) { IRQ(0x66, ref aContext); }
        public static void HandleInterrupt_67(ref IRQContext aContext) { IRQ(0x67, ref aContext); }
        public static void HandleInterrupt_68(ref IRQContext aContext) { IRQ(0x68, ref aContext); }
        public static void HandleInterrupt_69(ref IRQContext aContext) { IRQ(0x69, ref aContext); }
        public static void HandleInterrupt_6A(ref IRQContext aContext) { IRQ(0x6A, ref aContext); }
        public static void HandleInterrupt_6B(ref IRQContext aContext) { IRQ(0x6B, ref aContext); }
        public static void HandleInterrupt_6C(ref IRQContext aContext) { IRQ(0x6C, ref aContext); }
        public static void HandleInterrupt_6D(ref IRQContext aContext) { IRQ(0x6D, ref aContext); }
        public static void HandleInterrupt_6E(ref IRQContext aContext) { IRQ(0x6E, ref aContext); }
        public static void HandleInterrupt_6F(ref IRQContext aContext) { IRQ(0x6F, ref aContext); }
        public static void HandleInterrupt_70(ref IRQContext aContext) { IRQ(0x70, ref aContext); }
        public static void HandleInterrupt_71(ref IRQContext aContext) { IRQ(0x71, ref aContext); }
        public static void HandleInterrupt_72(ref IRQContext aContext) { IRQ(0x72, ref aContext); }
        public static void HandleInterrupt_73(ref IRQContext aContext) { IRQ(0x73, ref aContext); }
        public static void HandleInterrupt_74(ref IRQContext aContext) { IRQ(0x74, ref aContext); }
        public static void HandleInterrupt_75(ref IRQContext aContext) { IRQ(0x75, ref aContext); }
        public static void HandleInterrupt_76(ref IRQContext aContext) { IRQ(0x76, ref aContext); }
        public static void HandleInterrupt_77(ref IRQContext aContext) { IRQ(0x77, ref aContext); }
        public static void HandleInterrupt_78(ref IRQContext aContext) { IRQ(0x78, ref aContext); }
        public static void HandleInterrupt_79(ref IRQContext aContext) { IRQ(0x79, ref aContext); }
        public static void HandleInterrupt_7A(ref IRQContext aContext) { IRQ(0x7A, ref aContext); }
        public static void HandleInterrupt_7B(ref IRQContext aContext) { IRQ(0x7B, ref aContext); }
        public static void HandleInterrupt_7C(ref IRQContext aContext) { IRQ(0x7C, ref aContext); }
        public static void HandleInterrupt_7D(ref IRQContext aContext) { IRQ(0x7D, ref aContext); }
        public static void HandleInterrupt_7E(ref IRQContext aContext) { IRQ(0x7E, ref aContext); }
        public static void HandleInterrupt_7F(ref IRQContext aContext) { IRQ(0x7F, ref aContext); }
        public static void HandleInterrupt_80(ref IRQContext aContext) { IRQ(0x80, ref aContext); }
        public static void HandleInterrupt_81(ref IRQContext aContext) { IRQ(0x81, ref aContext); }
        public static void HandleInterrupt_82(ref IRQContext aContext) { IRQ(0x82, ref aContext); }
        public static void HandleInterrupt_83(ref IRQContext aContext) { IRQ(0x83, ref aContext); }
        public static void HandleInterrupt_84(ref IRQContext aContext) { IRQ(0x84, ref aContext); }
        public static void HandleInterrupt_85(ref IRQContext aContext) { IRQ(0x85, ref aContext); }
        public static void HandleInterrupt_86(ref IRQContext aContext) { IRQ(0x86, ref aContext); }
        public static void HandleInterrupt_87(ref IRQContext aContext) { IRQ(0x87, ref aContext); }
        public static void HandleInterrupt_88(ref IRQContext aContext) { IRQ(0x88, ref aContext); }
        public static void HandleInterrupt_89(ref IRQContext aContext) { IRQ(0x89, ref aContext); }
        public static void HandleInterrupt_8A(ref IRQContext aContext) { IRQ(0x8A, ref aContext); }
        public static void HandleInterrupt_8B(ref IRQContext aContext) { IRQ(0x8B, ref aContext); }
        public static void HandleInterrupt_8C(ref IRQContext aContext) { IRQ(0x8C, ref aContext); }
        public static void HandleInterrupt_8D(ref IRQContext aContext) { IRQ(0x8D, ref aContext); }
        public static void HandleInterrupt_8E(ref IRQContext aContext) { IRQ(0x8E, ref aContext); }
        public static void HandleInterrupt_8F(ref IRQContext aContext) { IRQ(0x8F, ref aContext); }
        public static void HandleInterrupt_90(ref IRQContext aContext) { IRQ(0x90, ref aContext); }
        public static void HandleInterrupt_91(ref IRQContext aContext) { IRQ(0x91, ref aContext); }
        public static void HandleInterrupt_92(ref IRQContext aContext) { IRQ(0x92, ref aContext); }
        public static void HandleInterrupt_93(ref IRQContext aContext) { IRQ(0x93, ref aContext); }
        public static void HandleInterrupt_94(ref IRQContext aContext) { IRQ(0x94, ref aContext); }
        public static void HandleInterrupt_95(ref IRQContext aContext) { IRQ(0x95, ref aContext); }
        public static void HandleInterrupt_96(ref IRQContext aContext) { IRQ(0x96, ref aContext); }
        public static void HandleInterrupt_97(ref IRQContext aContext) { IRQ(0x97, ref aContext); }
        public static void HandleInterrupt_98(ref IRQContext aContext) { IRQ(0x98, ref aContext); }
        public static void HandleInterrupt_99(ref IRQContext aContext) { IRQ(0x99, ref aContext); }
        public static void HandleInterrupt_9A(ref IRQContext aContext) { IRQ(0x9A, ref aContext); }
        public static void HandleInterrupt_9B(ref IRQContext aContext) { IRQ(0x9B, ref aContext); }
        public static void HandleInterrupt_9C(ref IRQContext aContext) { IRQ(0x9C, ref aContext); }
        public static void HandleInterrupt_9D(ref IRQContext aContext) { IRQ(0x9D, ref aContext); }
        public static void HandleInterrupt_9E(ref IRQContext aContext) { IRQ(0x9E, ref aContext); }
        public static void HandleInterrupt_9F(ref IRQContext aContext) { IRQ(0x9F, ref aContext); }
        public static void HandleInterrupt_A0(ref IRQContext aContext) { IRQ(0xA0, ref aContext); }
        public static void HandleInterrupt_A1(ref IRQContext aContext) { IRQ(0xA1, ref aContext); }
        public static void HandleInterrupt_A2(ref IRQContext aContext) { IRQ(0xA2, ref aContext); }
        public static void HandleInterrupt_A3(ref IRQContext aContext) { IRQ(0xA3, ref aContext); }
        public static void HandleInterrupt_A4(ref IRQContext aContext) { IRQ(0xA4, ref aContext); }
        public static void HandleInterrupt_A5(ref IRQContext aContext) { IRQ(0xA5, ref aContext); }
        public static void HandleInterrupt_A6(ref IRQContext aContext) { IRQ(0xA6, ref aContext); }
        public static void HandleInterrupt_A7(ref IRQContext aContext) { IRQ(0xA7, ref aContext); }
        public static void HandleInterrupt_A8(ref IRQContext aContext) { IRQ(0xA8, ref aContext); }
        public static void HandleInterrupt_A9(ref IRQContext aContext) { IRQ(0xA9, ref aContext); }
        public static void HandleInterrupt_AA(ref IRQContext aContext) { IRQ(0xAA, ref aContext); }
        public static void HandleInterrupt_AB(ref IRQContext aContext) { IRQ(0xAB, ref aContext); }
        public static void HandleInterrupt_AC(ref IRQContext aContext) { IRQ(0xAC, ref aContext); }
        public static void HandleInterrupt_AD(ref IRQContext aContext) { IRQ(0xAD, ref aContext); }
        public static void HandleInterrupt_AE(ref IRQContext aContext) { IRQ(0xAE, ref aContext); }
        public static void HandleInterrupt_AF(ref IRQContext aContext) { IRQ(0xAF, ref aContext); }
        public static void HandleInterrupt_B0(ref IRQContext aContext) { IRQ(0xB0, ref aContext); }
        public static void HandleInterrupt_B1(ref IRQContext aContext) { IRQ(0xB1, ref aContext); }
        public static void HandleInterrupt_B2(ref IRQContext aContext) { IRQ(0xB2, ref aContext); }
        public static void HandleInterrupt_B3(ref IRQContext aContext) { IRQ(0xB3, ref aContext); }
        public static void HandleInterrupt_B4(ref IRQContext aContext) { IRQ(0xB4, ref aContext); }
        public static void HandleInterrupt_B5(ref IRQContext aContext) { IRQ(0xB5, ref aContext); }
        public static void HandleInterrupt_B6(ref IRQContext aContext) { IRQ(0xB6, ref aContext); }
        public static void HandleInterrupt_B7(ref IRQContext aContext) { IRQ(0xB7, ref aContext); }
        public static void HandleInterrupt_B8(ref IRQContext aContext) { IRQ(0xB8, ref aContext); }
        public static void HandleInterrupt_B9(ref IRQContext aContext) { IRQ(0xB9, ref aContext); }
        public static void HandleInterrupt_BA(ref IRQContext aContext) { IRQ(0xBA, ref aContext); }
        public static void HandleInterrupt_BB(ref IRQContext aContext) { IRQ(0xBB, ref aContext); }
        public static void HandleInterrupt_BC(ref IRQContext aContext) { IRQ(0xBC, ref aContext); }
        public static void HandleInterrupt_BD(ref IRQContext aContext) { IRQ(0xBD, ref aContext); }
        public static void HandleInterrupt_BE(ref IRQContext aContext) { IRQ(0xBE, ref aContext); }
        public static void HandleInterrupt_BF(ref IRQContext aContext) { IRQ(0xBF, ref aContext); }
        public static void HandleInterrupt_C0(ref IRQContext aContext) { IRQ(0xC0, ref aContext); }
        public static void HandleInterrupt_C1(ref IRQContext aContext) { IRQ(0xC1, ref aContext); }
        public static void HandleInterrupt_C2(ref IRQContext aContext) { IRQ(0xC2, ref aContext); }
        public static void HandleInterrupt_C3(ref IRQContext aContext) { IRQ(0xC3, ref aContext); }
        public static void HandleInterrupt_C4(ref IRQContext aContext) { IRQ(0xC4, ref aContext); }
        public static void HandleInterrupt_C5(ref IRQContext aContext) { IRQ(0xC5, ref aContext); }
        public static void HandleInterrupt_C6(ref IRQContext aContext) { IRQ(0xC6, ref aContext); }
        public static void HandleInterrupt_C7(ref IRQContext aContext) { IRQ(0xC7, ref aContext); }
        public static void HandleInterrupt_C8(ref IRQContext aContext) { IRQ(0xC8, ref aContext); }
        public static void HandleInterrupt_C9(ref IRQContext aContext) { IRQ(0xC9, ref aContext); }
        public static void HandleInterrupt_CA(ref IRQContext aContext) { IRQ(0xCA, ref aContext); }
        public static void HandleInterrupt_CB(ref IRQContext aContext) { IRQ(0xCB, ref aContext); }
        public static void HandleInterrupt_CC(ref IRQContext aContext) { IRQ(0xCC, ref aContext); }
        public static void HandleInterrupt_CD(ref IRQContext aContext) { IRQ(0xCD, ref aContext); }
        public static void HandleInterrupt_CE(ref IRQContext aContext) { IRQ(0xCE, ref aContext); }
        public static void HandleInterrupt_CF(ref IRQContext aContext) { IRQ(0xCF, ref aContext); }
        public static void HandleInterrupt_D0(ref IRQContext aContext) { IRQ(0xD0, ref aContext); }
        public static void HandleInterrupt_D1(ref IRQContext aContext) { IRQ(0xD1, ref aContext); }
        public static void HandleInterrupt_D2(ref IRQContext aContext) { IRQ(0xD2, ref aContext); }
        public static void HandleInterrupt_D3(ref IRQContext aContext) { IRQ(0xD3, ref aContext); }
        public static void HandleInterrupt_D4(ref IRQContext aContext) { IRQ(0xD4, ref aContext); }
        public static void HandleInterrupt_D5(ref IRQContext aContext) { IRQ(0xD5, ref aContext); }
        public static void HandleInterrupt_D6(ref IRQContext aContext) { IRQ(0xD6, ref aContext); }
        public static void HandleInterrupt_D7(ref IRQContext aContext) { IRQ(0xD7, ref aContext); }
        public static void HandleInterrupt_D8(ref IRQContext aContext) { IRQ(0xD8, ref aContext); }
        public static void HandleInterrupt_D9(ref IRQContext aContext) { IRQ(0xD9, ref aContext); }
        public static void HandleInterrupt_DA(ref IRQContext aContext) { IRQ(0xDA, ref aContext); }
        public static void HandleInterrupt_DB(ref IRQContext aContext) { IRQ(0xDB, ref aContext); }
        public static void HandleInterrupt_DC(ref IRQContext aContext) { IRQ(0xDC, ref aContext); }
        public static void HandleInterrupt_DD(ref IRQContext aContext) { IRQ(0xDD, ref aContext); }
        public static void HandleInterrupt_DE(ref IRQContext aContext) { IRQ(0xDE, ref aContext); }
        public static void HandleInterrupt_DF(ref IRQContext aContext) { IRQ(0xDF, ref aContext); }
        public static void HandleInterrupt_E0(ref IRQContext aContext) { IRQ(0xE0, ref aContext); }
        public static void HandleInterrupt_E1(ref IRQContext aContext) { IRQ(0xE1, ref aContext); }
        public static void HandleInterrupt_E2(ref IRQContext aContext) { IRQ(0xE2, ref aContext); }
        public static void HandleInterrupt_E3(ref IRQContext aContext) { IRQ(0xE3, ref aContext); }
        public static void HandleInterrupt_E4(ref IRQContext aContext) { IRQ(0xE4, ref aContext); }
        public static void HandleInterrupt_E5(ref IRQContext aContext) { IRQ(0xE5, ref aContext); }
        public static void HandleInterrupt_E6(ref IRQContext aContext) { IRQ(0xE6, ref aContext); }
        public static void HandleInterrupt_E7(ref IRQContext aContext) { IRQ(0xE7, ref aContext); }
        public static void HandleInterrupt_E8(ref IRQContext aContext) { IRQ(0xE8, ref aContext); }
        public static void HandleInterrupt_E9(ref IRQContext aContext) { IRQ(0xE9, ref aContext); }
        public static void HandleInterrupt_EA(ref IRQContext aContext) { IRQ(0xEA, ref aContext); }
        public static void HandleInterrupt_EB(ref IRQContext aContext) { IRQ(0xEB, ref aContext); }
        public static void HandleInterrupt_EC(ref IRQContext aContext) { IRQ(0xEC, ref aContext); }
        public static void HandleInterrupt_ED(ref IRQContext aContext) { IRQ(0xED, ref aContext); }
        public static void HandleInterrupt_EE(ref IRQContext aContext) { IRQ(0xEE, ref aContext); }
        public static void HandleInterrupt_EF(ref IRQContext aContext) { IRQ(0xEF, ref aContext); }
        public static void HandleInterrupt_F0(ref IRQContext aContext) { IRQ(0xF0, ref aContext); }
        public static void HandleInterrupt_F1(ref IRQContext aContext) { IRQ(0xF1, ref aContext); }
        public static void HandleInterrupt_F2(ref IRQContext aContext) { IRQ(0xF2, ref aContext); }
        public static void HandleInterrupt_F3(ref IRQContext aContext) { IRQ(0xF3, ref aContext); }
        public static void HandleInterrupt_F4(ref IRQContext aContext) { IRQ(0xF4, ref aContext); }
        public static void HandleInterrupt_F5(ref IRQContext aContext) { IRQ(0xF5, ref aContext); }
        public static void HandleInterrupt_F6(ref IRQContext aContext) { IRQ(0xF6, ref aContext); }
        public static void HandleInterrupt_F7(ref IRQContext aContext) { IRQ(0xF7, ref aContext); }
        public static void HandleInterrupt_F8(ref IRQContext aContext) { IRQ(0xF8, ref aContext); }
        public static void HandleInterrupt_F9(ref IRQContext aContext) { IRQ(0xF9, ref aContext); }
        public static void HandleInterrupt_FA(ref IRQContext aContext) { IRQ(0xFA, ref aContext); }
        public static void HandleInterrupt_FB(ref IRQContext aContext) { IRQ(0xFB, ref aContext); }
        public static void HandleInterrupt_FC(ref IRQContext aContext) { IRQ(0xFC, ref aContext); }
        public static void HandleInterrupt_FD(ref IRQContext aContext) { IRQ(0xFD, ref aContext); }
        public static void HandleInterrupt_FE(ref IRQContext aContext) { IRQ(0xFE, ref aContext); }
        public static void HandleInterrupt_FF(ref IRQContext aContext) { IRQ(0xFF, ref aContext); }

        #endregion

        #region CPU Exceptions

        /// <summary>
        /// General protection fault IRQ delegate.
        /// </summary>
        public static IRQDelegate GeneralProtectionFault;

        /// <summary>
        /// Divide By Zero Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_00(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Divide by zero", "EDivideByZero", ref aContext, aContext.EIP);
        }

        /// <summary>
        /// Debug Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_01(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Debug Exception", "Debug Exception", ref aContext);
        }

        /// <summary>
        /// Non Maskable Interrupt Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_02(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Non Maskable Interrupt Exception", "Non Maskable Interrupt Exception", ref aContext);
        }

        /// <summary>
        /// Breakpoint Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_03(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Breakpoint Exception", "Breakpoint Exception", ref aContext);
        }

        /// <summary>
        /// Into Detected Overflow Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_04(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Into Detected Overflow Exception", "Into Detected Overflow Exception", ref aContext);
        }

        /// <summary>
        /// Out of Bounds Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_05(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Out of Bounds Exception", "Out of Bounds Exception", ref aContext);
        }

        /// <summary>
        /// Invalid Opcode.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_06(ref IRQContext aContext) {
            // although mLastKnownAddress is a static, we need to get it here, any subsequent calls will change the value!!!
            var xLastKnownAddress = mLastKnownAddress;
            HandleException(aContext.EIP, "Invalid Opcode", "EInvalidOpcode", ref aContext, xLastKnownAddress);
        }

        /// <summary>
        /// No Coprocessor Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_07(ref IRQContext aContext) {
            HandleException(aContext.EIP, "No Coprocessor Exception", "No Coprocessor Exception", ref aContext);
        }

        /// <summary>
        /// Double Fault Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_08(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Double Fault Exception", "Double Fault Exception", ref aContext);
        }

        /// <summary>
        /// Coprocessor Segment Overrun Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_09(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Coprocessor Segment Overrun Exception", "Coprocessor Segment Overrun Exception", ref aContext);
        }

        /// <summary>
        /// Bad TSS Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_0A(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Bad TSS Exception", "Bad TSS Exception", ref aContext);
        }

        /// <summary>
        /// Segment Not Present.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_0B(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Segment Not Present", "Segment Not Present", ref aContext);
        }

        /// <summary>
        /// Stack Fault Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_0C(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Stack Fault Exception", "Stack Fault Exception", ref aContext);
        }

        /// <summary>
        /// General Protection Fault.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_0D(ref IRQContext aContext) {
            if (GeneralProtectionFault != null) {
                GeneralProtectionFault(ref aContext);
            } else {
                HandleException(aContext.EIP, "General Protection Fault", "GPF", ref aContext);
            }
        }

        /// <summary>
        /// Page Fault Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_0E(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Page Fault Exception", "Page Fault Exception", ref aContext);
        }

        /// <summary>
        /// Unknown Interrupt Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_0F(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Unknown Interrupt Exception", "Unknown Interrupt Exception", ref aContext);
        }

        /// <summary>
        /// x87 Floating Point Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_10(ref IRQContext aContext) {
            HandleException(aContext.EIP, "x87 Floating Point Exception", "Coprocessor Fault Exception", ref aContext);
        }

        /// <summary>
        /// Alignment Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_11(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Alignment Exception", "Alignment Exception", ref aContext);
        }

        /// <summary>
        /// Machine Check Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_12(ref IRQContext aContext) {
            HandleException(aContext.EIP, "Machine Check Exception", "Machine Check Exception", ref aContext);
        }

        /// <summary>
        /// SIMD Floating Point Exception.
        /// </summary>
        /// <param name="aContext">IRQ context.</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void HandleInterrupt_13(ref IRQContext aContext) {
            HandleException(aContext.EIP, "SIMD Floating Point Exception", "SIMD Floating Point Exception", ref aContext);
        }


        #endregion

        /// <summary>
        /// Handle exception.
        /// </summary>
        /// <param name="aEIP">Unused.</param>
        /// <param name="aDescription">Unused.</param>
        /// <param name="aName">Unused.</param>
        /// <param name="ctx">IRQ context.</param>
        /// <param name="lastKnownAddressValue">Last known address value. (default = 0)</param>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        private static void HandleException(uint aEIP, string aDescription, string aName, ref IRQContext ctx, uint lastKnownAddressValue = 0) {
            // At this point we are in a very unstable state.
            // Try not to use any Cosmos routines, just
            // report a crash dump.
            const string xHex = "0123456789ABCDEF";
            uint xPtr = ctx.EIP;

            // we're printing exception info to the screen now:
            // 0/0: x
            // 1/0: exception number in hex
            unsafe
            {
                byte* xAddress = (byte*)0xB8000;

                PutErrorString(0, 0, "Cosmos CPU Exception");

                PutErrorString(2, 0, "Error Code: 0x");
                PutErrorChar(2, 14, xHex[(int)((ctx.Interrupt >> 4) & 0xF)]);
                PutErrorChar(2, 15, xHex[(int)(ctx.Interrupt & 0xF)]);

                PutErrorString(2, 0, aName);
                PutErrorString(3, 0, aDescription);

                if (lastKnownAddressValue != 0) {
                    PutErrorString(1, 0, "Last known address: 0x");

                    PutErrorChar(1, 22, xHex[(int)((lastKnownAddressValue >> 28) & 0xF)]);
                    PutErrorChar(1, 23, xHex[(int)((lastKnownAddressValue >> 24) & 0xF)]);
                    PutErrorChar(1, 24, xHex[(int)((lastKnownAddressValue >> 20) & 0xF)]);
                    PutErrorChar(1, 25, xHex[(int)((lastKnownAddressValue >> 16) & 0xF)]);
                    PutErrorChar(1, 26, xHex[(int)((lastKnownAddressValue >> 12) & 0xF)]);
                    PutErrorChar(1, 27, xHex[(int)((lastKnownAddressValue >> 8) & 0xF)]);
                    PutErrorChar(1, 28, xHex[(int)((lastKnownAddressValue >> 4) & 0xF)]);
                    PutErrorChar(1, 29, xHex[(int)(lastKnownAddressValue & 0xF)]);
                }

            }

            // lock up
            while (true) {
            }
        }

        /// <summary>
        /// Put error char.
        /// </summary>
        /// <param name="line">Line to put the error char at.</param>
        /// <param name="col">Column to put the error char at.</param>
        /// <param name="c">Char to put.</param>
        private static void PutErrorChar(int line, int col, char c) {
            unsafe
            {
                byte* xAddress = (byte*)0xB8000;

                xAddress += (line * 80 + col) * 2;

                xAddress[0] = (byte)c;
                xAddress[1] = 0x0C;
            }
        }

        /// <summary>
        /// Put error string.
        /// </summary>
        /// <param name="line">Line to put the error string at.</param>
        /// <param name="startCol">Starting column to put the error string at.</param>
        /// <param name="error">Error string to put.</param>
        /// <exception cref="System.OverflowException">Thrown if error length in greater then Int32.MaxValue.</exception>
        private static void PutErrorString(int line, int startCol, string error) {
            for (int i = 0; i < error.Length; i++) {
                PutErrorChar(line, startCol + i, error[i]);
            }
        }

        // This is to trick IL2CPU to compile it in
        //TODO: Make a new attribute that IL2CPU sees when scanning to force inclusion so we dont have to do this.
        // We dont actually need to call this method
        /// <summary>
        /// Dummy function, used by the bootstrap.
        /// </summary>
        /// <remarks>This is to trick IL2CPU to compile it in.</remarks>
        /// <exception cref="System.IndexOutOfRangeException">Thrown on fatal error, contact support.</exception>
        /// <exception cref="System.OverflowException">Thrown on fatal error, contact support.</exception>
        public static void Dummy() {
            // Compiler magic
            bool xTest = false;
            if (xTest) {
                unsafe
                {
                    var xCtx = new IRQContext();
                    HandleInterrupt_Default(ref xCtx);
                    HandleInterrupt_00(ref xCtx);
                    HandleInterrupt_01(ref xCtx);
                    HandleInterrupt_02(ref xCtx);
                    HandleInterrupt_03(ref xCtx);
                    HandleInterrupt_04(ref xCtx);
                    HandleInterrupt_05(ref xCtx);
                    HandleInterrupt_06(ref xCtx);
                    HandleInterrupt_07(ref xCtx);
                    HandleInterrupt_08(ref xCtx);
                    HandleInterrupt_09(ref xCtx);
                    HandleInterrupt_0A(ref xCtx);
                    HandleInterrupt_0B(ref xCtx);
                    HandleInterrupt_0C(ref xCtx);
                    HandleInterrupt_0D(ref xCtx);
                    HandleInterrupt_0E(ref xCtx);
                    HandleInterrupt_0F(ref xCtx);
                    HandleInterrupt_10(ref xCtx);
                    HandleInterrupt_11(ref xCtx);
                    HandleInterrupt_12(ref xCtx);
                    HandleInterrupt_13(ref xCtx);
                    HandleInterrupt_20(ref xCtx);
                    HandleInterrupt_21(ref xCtx);
                    HandleInterrupt_22(ref xCtx);
                    HandleInterrupt_23(ref xCtx);
                    HandleInterrupt_24(ref xCtx);
                    HandleInterrupt_25(ref xCtx);
                    HandleInterrupt_26(ref xCtx);
                    HandleInterrupt_27(ref xCtx);
                    HandleInterrupt_28(ref xCtx);
                    HandleInterrupt_29(ref xCtx);
                    HandleInterrupt_2A(ref xCtx);
                    HandleInterrupt_2B(ref xCtx);
                    HandleInterrupt_2C(ref xCtx);
                    HandleInterrupt_2D(ref xCtx);
                    HandleInterrupt_2E(ref xCtx);
                    HandleInterrupt_2F(ref xCtx);
                    HandleInterrupt_30(ref xCtx);
                    HandleInterrupt_31(ref xCtx);
                    HandleInterrupt_32(ref xCtx);
                    HandleInterrupt_33(ref xCtx);
                    HandleInterrupt_34(ref xCtx);
                    HandleInterrupt_35(ref xCtx);
                    HandleInterrupt_36(ref xCtx);
                    HandleInterrupt_37(ref xCtx);
                    HandleInterrupt_38(ref xCtx);
                    HandleInterrupt_39(ref xCtx);
                    HandleInterrupt_3A(ref xCtx);
                    HandleInterrupt_3B(ref xCtx);
                    HandleInterrupt_3C(ref xCtx);
                    HandleInterrupt_3D(ref xCtx);
                    HandleInterrupt_3E(ref xCtx);
                    HandleInterrupt_3F(ref xCtx);
                    HandleInterrupt_40(ref xCtx);
                    HandleInterrupt_41(ref xCtx);
                    HandleInterrupt_42(ref xCtx);
                    HandleInterrupt_43(ref xCtx);
                    HandleInterrupt_44(ref xCtx);
                    HandleInterrupt_45(ref xCtx);
                    HandleInterrupt_46(ref xCtx);
                    HandleInterrupt_47(ref xCtx);
                    HandleInterrupt_48(ref xCtx);
                    HandleInterrupt_49(ref xCtx);
                    HandleInterrupt_4A(ref xCtx);
                    HandleInterrupt_4B(ref xCtx);
                    HandleInterrupt_4C(ref xCtx);
                    HandleInterrupt_4D(ref xCtx);
                    HandleInterrupt_4E(ref xCtx);
                    HandleInterrupt_4F(ref xCtx);
                    HandleInterrupt_50(ref xCtx);
                    HandleInterrupt_51(ref xCtx);
                    HandleInterrupt_52(ref xCtx);
                    HandleInterrupt_53(ref xCtx);
                    HandleInterrupt_54(ref xCtx);
                    HandleInterrupt_55(ref xCtx);
                    HandleInterrupt_56(ref xCtx);
                    HandleInterrupt_57(ref xCtx);
                    HandleInterrupt_58(ref xCtx);
                    HandleInterrupt_59(ref xCtx);
                    HandleInterrupt_5A(ref xCtx);
                    HandleInterrupt_5B(ref xCtx);
                    HandleInterrupt_5C(ref xCtx);
                    HandleInterrupt_5D(ref xCtx);
                    HandleInterrupt_5E(ref xCtx);
                    HandleInterrupt_5F(ref xCtx);
                    HandleInterrupt_60(ref xCtx);
                    HandleInterrupt_61(ref xCtx);
                    HandleInterrupt_62(ref xCtx);
                    HandleInterrupt_63(ref xCtx);
                    HandleInterrupt_64(ref xCtx);
                    HandleInterrupt_65(ref xCtx);
                    HandleInterrupt_66(ref xCtx);
                    HandleInterrupt_67(ref xCtx);
                    HandleInterrupt_68(ref xCtx);
                    HandleInterrupt_69(ref xCtx);
                    HandleInterrupt_6A(ref xCtx);
                    HandleInterrupt_6B(ref xCtx);
                    HandleInterrupt_6C(ref xCtx);
                    HandleInterrupt_6D(ref xCtx);
                    HandleInterrupt_6E(ref xCtx);
                    HandleInterrupt_6F(ref xCtx);
                    HandleInterrupt_70(ref xCtx);
                    HandleInterrupt_71(ref xCtx);
                    HandleInterrupt_72(ref xCtx);
                    HandleInterrupt_73(ref xCtx);
                    HandleInterrupt_74(ref xCtx);
                    HandleInterrupt_75(ref xCtx);
                    HandleInterrupt_76(ref xCtx);
                    HandleInterrupt_77(ref xCtx);
                    HandleInterrupt_78(ref xCtx);
                    HandleInterrupt_79(ref xCtx);
                    HandleInterrupt_7A(ref xCtx);
                    HandleInterrupt_7B(ref xCtx);
                    HandleInterrupt_7C(ref xCtx);
                    HandleInterrupt_7D(ref xCtx);
                    HandleInterrupt_7E(ref xCtx);
                    HandleInterrupt_7F(ref xCtx);
                    HandleInterrupt_80(ref xCtx);
                    HandleInterrupt_81(ref xCtx);
                    HandleInterrupt_82(ref xCtx);
                    HandleInterrupt_83(ref xCtx);
                    HandleInterrupt_84(ref xCtx);
                    HandleInterrupt_85(ref xCtx);
                    HandleInterrupt_86(ref xCtx);
                    HandleInterrupt_87(ref xCtx);
                    HandleInterrupt_88(ref xCtx);
                    HandleInterrupt_89(ref xCtx);
                    HandleInterrupt_8A(ref xCtx);
                    HandleInterrupt_8B(ref xCtx);
                    HandleInterrupt_8C(ref xCtx);
                    HandleInterrupt_8D(ref xCtx);
                    HandleInterrupt_8E(ref xCtx);
                    HandleInterrupt_8F(ref xCtx);
                    HandleInterrupt_90(ref xCtx);
                    HandleInterrupt_91(ref xCtx);
                    HandleInterrupt_92(ref xCtx);
                    HandleInterrupt_93(ref xCtx);
                    HandleInterrupt_94(ref xCtx);
                    HandleInterrupt_95(ref xCtx);
                    HandleInterrupt_96(ref xCtx);
                    HandleInterrupt_97(ref xCtx);
                    HandleInterrupt_98(ref xCtx);
                    HandleInterrupt_99(ref xCtx);
                    HandleInterrupt_9A(ref xCtx);
                    HandleInterrupt_9B(ref xCtx);
                    HandleInterrupt_9C(ref xCtx);
                    HandleInterrupt_9D(ref xCtx);
                    HandleInterrupt_9E(ref xCtx);
                    HandleInterrupt_9F(ref xCtx);
                    HandleInterrupt_A0(ref xCtx);
                    HandleInterrupt_A1(ref xCtx);
                    HandleInterrupt_A2(ref xCtx);
                    HandleInterrupt_A3(ref xCtx);
                    HandleInterrupt_A4(ref xCtx);
                    HandleInterrupt_A5(ref xCtx);
                    HandleInterrupt_A6(ref xCtx);
                    HandleInterrupt_A7(ref xCtx);
                    HandleInterrupt_A8(ref xCtx);
                    HandleInterrupt_A9(ref xCtx);
                    HandleInterrupt_AA(ref xCtx);
                    HandleInterrupt_AB(ref xCtx);
                    HandleInterrupt_AC(ref xCtx);
                    HandleInterrupt_AD(ref xCtx);
                    HandleInterrupt_AE(ref xCtx);
                    HandleInterrupt_AF(ref xCtx);
                    HandleInterrupt_B0(ref xCtx);
                    HandleInterrupt_B1(ref xCtx);
                    HandleInterrupt_B2(ref xCtx);
                    HandleInterrupt_B3(ref xCtx);
                    HandleInterrupt_B4(ref xCtx);
                    HandleInterrupt_B5(ref xCtx);
                    HandleInterrupt_B6(ref xCtx);
                    HandleInterrupt_B7(ref xCtx);
                    HandleInterrupt_B8(ref xCtx);
                    HandleInterrupt_B9(ref xCtx);
                    HandleInterrupt_BA(ref xCtx);
                    HandleInterrupt_BB(ref xCtx);
                    HandleInterrupt_BC(ref xCtx);
                    HandleInterrupt_BD(ref xCtx);
                    HandleInterrupt_BE(ref xCtx);
                    HandleInterrupt_BF(ref xCtx);
                    HandleInterrupt_C0(ref xCtx);
                    HandleInterrupt_C1(ref xCtx);
                    HandleInterrupt_C2(ref xCtx);
                    HandleInterrupt_C3(ref xCtx);
                    HandleInterrupt_C4(ref xCtx);
                    HandleInterrupt_C5(ref xCtx);
                    HandleInterrupt_C6(ref xCtx);
                    HandleInterrupt_C7(ref xCtx);
                    HandleInterrupt_C8(ref xCtx);
                    HandleInterrupt_C9(ref xCtx);
                    HandleInterrupt_CA(ref xCtx);
                    HandleInterrupt_CB(ref xCtx);
                    HandleInterrupt_CC(ref xCtx);
                    HandleInterrupt_CD(ref xCtx);
                    HandleInterrupt_CE(ref xCtx);
                    HandleInterrupt_CF(ref xCtx);
                    HandleInterrupt_D0(ref xCtx);
                    HandleInterrupt_D1(ref xCtx);
                    HandleInterrupt_D2(ref xCtx);
                    HandleInterrupt_D3(ref xCtx);
                    HandleInterrupt_D4(ref xCtx);
                    HandleInterrupt_D5(ref xCtx);
                    HandleInterrupt_D6(ref xCtx);
                    HandleInterrupt_D7(ref xCtx);
                    HandleInterrupt_D8(ref xCtx);
                    HandleInterrupt_D9(ref xCtx);
                    HandleInterrupt_DA(ref xCtx);
                    HandleInterrupt_DB(ref xCtx);
                    HandleInterrupt_DC(ref xCtx);
                    HandleInterrupt_DD(ref xCtx);
                    HandleInterrupt_DE(ref xCtx);
                    HandleInterrupt_DF(ref xCtx);
                    HandleInterrupt_E0(ref xCtx);
                    HandleInterrupt_E1(ref xCtx);
                    HandleInterrupt_E2(ref xCtx);
                    HandleInterrupt_E3(ref xCtx);
                    HandleInterrupt_E4(ref xCtx);
                    HandleInterrupt_E5(ref xCtx);
                    HandleInterrupt_E6(ref xCtx);
                    HandleInterrupt_E7(ref xCtx);
                    HandleInterrupt_E8(ref xCtx);
                    HandleInterrupt_E9(ref xCtx);
                    HandleInterrupt_EA(ref xCtx);
                    HandleInterrupt_EB(ref xCtx);
                    HandleInterrupt_EC(ref xCtx);
                    HandleInterrupt_ED(ref xCtx);
                    HandleInterrupt_EE(ref xCtx);
                    HandleInterrupt_EF(ref xCtx);
                    HandleInterrupt_F0(ref xCtx);
                    HandleInterrupt_F1(ref xCtx);
                    HandleInterrupt_F2(ref xCtx);
                    HandleInterrupt_F3(ref xCtx);
                    HandleInterrupt_F4(ref xCtx);
                    HandleInterrupt_F5(ref xCtx);
                    HandleInterrupt_F6(ref xCtx);
                    HandleInterrupt_F7(ref xCtx);
                    HandleInterrupt_F8(ref xCtx);
                    HandleInterrupt_F9(ref xCtx);
                    HandleInterrupt_FA(ref xCtx);
                    HandleInterrupt_FB(ref xCtx);
                    HandleInterrupt_FC(ref xCtx);
                    HandleInterrupt_FD(ref xCtx);
                    HandleInterrupt_FE(ref xCtx);
                    HandleInterrupt_FF(ref xCtx);
                }
            }
        }

    }
}
