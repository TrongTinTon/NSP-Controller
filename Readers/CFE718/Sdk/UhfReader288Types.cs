using System;

namespace NSPGatekeeper.Controller.Readers.CFE718.Sdk
{
    internal enum UhfReader288ConnectionKind
    {
        None,
        Com,
        Tcp
    }

    internal sealed class UhfReader288Tag
    {
        public byte PacketParam { get; set; }
        public byte Length { get; set; }
        public string Uid { get; set; }
        public int PhaseBegin { get; set; }
        public int PhaseEnd { get; set; }
        public byte Rssi { get; set; }
        public int FrequencyKhz { get; set; }
        public byte Antenna { get; set; }
        public int Handles { get; set; }
    }

    internal sealed class UhfReader288InventoryRequest
    {
        public byte QValue { get; set; }
        public byte Session { get; set; }
        public byte MaskMemory { get; set; }
        public byte[] MaskAddress { get; set; }
        public byte MaskLength { get; set; }
        public byte[] MaskData { get; set; }
        public byte MaskFlag { get; set; }
        public byte TidAddress { get; set; }
        public byte TidLength { get; set; }
        public byte TidFlag { get; set; }
        public byte Target { get; set; }
        public byte AntennaSelector { get; set; }
        public byte ScanTime { get; set; }
        public byte FastFlag { get; set; }
        public byte[] OutputBuffer { get; set; }

        public UhfReader288InventoryRequest()
        {
            MaskAddress = new byte[2];
            MaskData = new byte[100];
            OutputBuffer = new byte[50000];
        }
    }

    internal sealed class UhfReader288InventoryResult
    {
        public int ResultCode { get; set; }
        public byte Antenna { get; set; }
        public int TotalLength { get; set; }
        public int TagCount { get; set; }
    }
}
