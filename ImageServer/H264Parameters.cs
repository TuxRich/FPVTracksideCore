using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ImageServer
{
    // An H.264 stream's parameter sets (SPS and PPS), and just enough of what's in them to rebuild a recording's MP4
    // index (see Mp4Repair): the picture size and frame rate, and whether a picture's first slice fits them, which
    // the slices of a stream made with other parameters very likely don't.
    public class H264Parameters
    {
        // NAL units, each starting with its header byte.
        public byte[] Sps { get; private set; }
        public byte[] Pps { get; private set; }

        public int Width { get; private set; }
        public int Height { get; private set; }

        // From the SPS's timing info, 0 without it.
        public double FrameRate { get; private set; }

        private int profile;
        private int spsId;
        private int chromaFormat;
        private bool separateColourPlane;
        private int bitDepthLuma;
        private int bitDepthChroma;
        private int log2MaxFrameNum;
        private int picOrderCntType;
        private int log2MaxPicOrderCntLsb;
        private bool deltaPicOrderAlwaysZero;
        private bool frameMbsOnly;

        private int ppsId;
        private int ppsSpsId;
        private bool entropyCodingMode;
        private bool bottomFieldPicOrderInFramePresent;
        private int sliceGroups;
        private int picInitQp;
        private bool deblockingFilterControlPresent;
        private bool redundantPicCntPresent;

        private H264Parameters()
        {
        }

        public static H264Parameters FromNals(byte[] sps, byte[] pps)
        {
            if (sps == null || pps == null || sps.Length < 4 || pps.Length < 2)
                return null;

            if ((sps[0] & 0x1F) != 7 || (pps[0] & 0x1F) != 8)
                return null;

            H264Parameters parameters = new H264Parameters() { Sps = sps, Pps = pps };
            try
            {
                parameters.ReadSps();
                parameters.ReadPps();
            }
            catch (Exception)
            {
                // Ran off the end, so it's not a parameter set we understand.
                return null;
            }

            if (parameters.ppsSpsId != parameters.spsId || parameters.Width <= 0 || parameters.Height <= 0)
                return null;

            return parameters;
        }

        // NAL units after start codes (00 00 01), as in MF_MT_MPEG_SEQUENCE_HEADER.
        public static H264Parameters FromAnnexB(byte[] data)
        {
            byte[] sps = null;
            byte[] pps = null;
            foreach (byte[] nal in SplitAnnexB(data))
            {
                int type = nal[0] & 0x1F;
                if (type == 7 && sps == null)
                    sps = nal;
                if (type == 8 && pps == null)
                    pps = nal;
            }
            return FromNals(sps, pps);
        }

        // An MP4 avcC box's contents.
        public static H264Parameters FromAvcC(byte[] avcC)
        {
            try
            {
                int position = 5;
                byte[] sps = null;
                int spsCount = avcC[position++] & 0x1F;
                for (int i = 0; i < spsCount; i++)
                {
                    byte[] nal = ReadLengthPrefixed(avcC, ref position);
                    sps = sps ?? nal;
                }

                byte[] pps = null;
                int ppsCount = avcC[position++];
                for (int i = 0; i < ppsCount; i++)
                {
                    byte[] nal = ReadLengthPrefixed(avcC, ref position);
                    pps = pps ?? nal;
                }

                return FromNals(sps, pps);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The avcC box's contents for these.
        public byte[] ToAvcC()
        {
            List<byte> avcC = new List<byte>() { 1, Sps[1], Sps[2], Sps[3], 0xFF, 0xE1 };
            avcC.Add((byte)(Sps.Length >> 8));
            avcC.Add((byte)Sps.Length);
            avcC.AddRange(Sps);
            avcC.Add(1);
            avcC.Add((byte)(Pps.Length >> 8));
            avcC.Add((byte)Pps.Length);
            avcC.AddRange(Pps);

            if (profile == 100 || profile == 110 || profile == 122 || profile == 144)
            {
                avcC.Add((byte)(0xFC | chromaFormat));
                avcC.Add((byte)(0xF8 | (bitDepthLuma - 8)));
                avcC.Add((byte)(0xF8 | (bitDepthChroma - 8)));
                avcC.Add(0);
            }
            return avcC.ToArray();
        }

        // An IDR picture's first slice, a NAL unit from its header byte on: whether the slice header parses as it
        // should with these, and the picture data after it starts where it should.
        public bool FitsIdrSlice(byte[] slice)
        {
            // Rare enough not to bother with.
            if (sliceGroups > 1 || separateColourPlane)
                return true;

            try
            {
                int nalRefIdc = (slice[0] >> 5) & 3;
                BitReader bits = new BitReader(slice);

                // first_mb_in_slice, slice_type (I or SI, as an IDR's are) and pic_parameter_set_id.
                if (bits.Ue() != 0)
                    return false;

                int sliceType = bits.Ue() % 5;
                if (sliceType != 2 && sliceType != 4)
                    return false;

                if (bits.Ue() != ppsId)
                    return false;

                // frame_num, which is 0 for an IDR.
                if (bits.U(log2MaxFrameNum) != 0)
                    return false;

                bool field = false;
                if (!frameMbsOnly)
                {
                    field = bits.U(1) == 1;
                    if (field)
                        bits.U(1);
                }

                // idr_pic_id
                if (bits.Ue() > 65535)
                    return false;

                if (picOrderCntType == 0)
                {
                    bits.U(log2MaxPicOrderCntLsb);
                    if (bottomFieldPicOrderInFramePresent && !field)
                        bits.Se();
                }
                else if (picOrderCntType == 1 && !deltaPicOrderAlwaysZero)
                {
                    bits.Se();
                    if (bottomFieldPicOrderInFramePresent && !field)
                        bits.Se();
                }

                if (redundantPicCntPresent)
                    bits.Ue();

                // dec_ref_pic_marking: no_output_of_prior_pics_flag, long_term_reference_flag.
                if (nalRefIdc != 0)
                {
                    bits.U(2);
                }

                int qp = picInitQp + bits.Se();
                if (qp < -6 * (bitDepthLuma - 8) || qp > 51)
                    return false;

                // slice_qs_delta
                if (sliceType == 4)
                    bits.Se();

                if (deblockingFilterControlPresent)
                {
                    int disableDeblocking = bits.Ue();
                    if (disableDeblocking > 2)
                        return false;

                    if (disableDeblocking != 1)
                    {
                        int alpha = bits.Se();
                        int beta = bits.Se();
                        if (alpha < -6 || alpha > 6 || beta < -6 || beta > 6)
                            return false;
                    }
                }

                // CABAC picture data starts byte aligned, after one bits.
                if (entropyCodingMode)
                {
                    while (!bits.ByteAligned)
                    {
                        if (bits.U(1) != 1)
                            return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReadSps()
        {
            BitReader bits = new BitReader(Sps);
            profile = (int)bits.U(8);
            bits.U(16); // constraint flags and level
            spsId = bits.Ue();

            chromaFormat = 1;
            bitDepthLuma = 8;
            bitDepthChroma = 8;
            if (profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 || profile == 83 || profile == 86
                || profile == 118 || profile == 128 || profile == 138 || profile == 139 || profile == 134 || profile == 135)
            {
                chromaFormat = bits.Ue();
                if (chromaFormat == 3)
                    separateColourPlane = bits.U(1) == 1;

                bitDepthLuma = bits.Ue() + 8;
                bitDepthChroma = bits.Ue() + 8;
                bits.U(1); // qpprime_y_zero_transform_bypass_flag

                // seq_scaling_matrix_present_flag
                if (bits.U(1) == 1)
                {
                    for (int i = 0; i < (chromaFormat != 3 ? 8 : 12); i++)
                    {
                        if (bits.U(1) == 1)
                        {
                            SkipScalingList(bits, i < 6 ? 16 : 64);
                        }
                    }
                }
            }

            log2MaxFrameNum = bits.Ue() + 4;
            picOrderCntType = bits.Ue();
            if (picOrderCntType == 0)
            {
                log2MaxPicOrderCntLsb = bits.Ue() + 4;
            }
            else if (picOrderCntType == 1)
            {
                deltaPicOrderAlwaysZero = bits.U(1) == 1;
                bits.Se();
                bits.Se();
                int cycle = bits.Ue();
                for (int i = 0; i < cycle; i++)
                {
                    bits.Se();
                }
            }

            bits.Ue(); // max_num_ref_frames
            bits.U(1); // gaps_in_frame_num_value_allowed_flag

            int widthInMbs = bits.Ue() + 1;
            int heightInMapUnits = bits.Ue() + 1;
            frameMbsOnly = bits.U(1) == 1;
            if (!frameMbsOnly)
                bits.U(1); // mb_adaptive_frame_field_flag
            bits.U(1); // direct_8x8_inference_flag

            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (bits.U(1) == 1)
            {
                cropLeft = bits.Ue();
                cropRight = bits.Ue();
                cropTop = bits.Ue();
                cropBottom = bits.Ue();
            }

            bool monochrome = chromaFormat == 0 || separateColourPlane;
            int cropUnitX = monochrome ? 1 : (chromaFormat == 3 ? 1 : 2);
            int cropUnitY = (monochrome ? 1 : (chromaFormat == 1 ? 2 : 1)) * (frameMbsOnly ? 1 : 2);

            Width = widthInMbs * 16 - cropUnitX * (cropLeft + cropRight);
            Height = heightInMapUnits * 16 * (frameMbsOnly ? 1 : 2) - cropUnitY * (cropTop + cropBottom);

            // vui_parameters_present_flag
            if (bits.U(1) == 1)
            {
                ReadVuiTiming(bits);
            }
        }

        private void ReadVuiTiming(BitReader bits)
        {
            // aspect_ratio_info_present_flag
            if (bits.U(1) == 1)
            {
                if (bits.U(8) == 255)
                    bits.U(32);
            }

            // overscan_info_present_flag
            if (bits.U(1) == 1)
                bits.U(1);

            // video_signal_type_present_flag
            if (bits.U(1) == 1)
            {
                bits.U(4);
                if (bits.U(1) == 1)
                    bits.U(24);
            }

            // chroma_loc_info_present_flag
            if (bits.U(1) == 1)
            {
                bits.Ue();
                bits.Ue();
            }

            // timing_info_present_flag
            if (bits.U(1) == 1)
            {
                long unitsInTick = bits.U(32);
                long timeScale = bits.U(32);
                if (unitsInTick > 0)
                {
                    FrameRate = timeScale / (2.0 * unitsInTick);
                }
            }
        }

        private void ReadPps()
        {
            BitReader bits = new BitReader(Pps);
            ppsId = bits.Ue();
            ppsSpsId = bits.Ue();
            entropyCodingMode = bits.U(1) == 1;
            bottomFieldPicOrderInFramePresent = bits.U(1) == 1;
            sliceGroups = bits.Ue() + 1;

            // Slices can't be checked against these then (see FitsIdrSlice).
            if (sliceGroups > 1)
                return;

            bits.Ue(); // num_ref_idx_l0_default_active_minus1
            bits.Ue(); // num_ref_idx_l1_default_active_minus1
            bits.U(3); // weighted_pred_flag, weighted_bipred_idc
            picInitQp = 26 + bits.Se();
            bits.Se(); // pic_init_qs_minus26
            bits.Se(); // chroma_qp_index_offset
            deblockingFilterControlPresent = bits.U(1) == 1;
            bits.U(1); // constrained_intra_pred_flag
            redundantPicCntPresent = bits.U(1) == 1;
        }

        private static void SkipScalingList(BitReader bits, int size)
        {
            int last = 8;
            int next = 8;
            for (int j = 0; j < size; j++)
            {
                if (next != 0)
                {
                    next = (last + bits.Se() + 256) % 256;
                }
                last = next == 0 ? last : next;
            }
        }

        private static byte[] ReadLengthPrefixed(byte[] data, ref int position)
        {
            int length = (data[position] << 8) | data[position + 1];
            position += 2;
            byte[] bytes = new byte[length];
            Array.Copy(data, position, bytes, 0, length);
            position += length;
            return bytes;
        }

        private static IEnumerable<byte[]> SplitAnnexB(byte[] data)
        {
            // Where each NAL unit starts, just after its start code.
            List<int> starts = new List<int>();
            for (int i = 0; i + 2 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    starts.Add(i + 3);
                    i += 2;
                }
            }

            for (int s = 0; s < starts.Count; s++)
            {
                int start = starts[s];
                int end = s + 1 < starts.Count ? starts[s + 1] - 3 : data.Length;

                // Not the zeros of a 4 byte start code, or trailing ones.
                while (end > start && data[end - 1] == 0)
                {
                    end--;
                }

                if (end > start)
                {
                    byte[] nal = new byte[end - start];
                    Array.Copy(data, start, nal, 0, nal.Length);
                    yield return nal;
                }
            }
        }

        // Reads a NAL unit's payload, without its header byte or the bytes that stop it containing start codes.
        private class BitReader
        {
            private byte[] data;
            private int position;

            public bool ByteAligned { get { return (position & 7) == 0; } }

            public BitReader(byte[] nal)
            {
                List<byte> payload = new List<byte>(nal.Length);
                int zeros = 0;
                for (int i = 1; i < nal.Length; i++)
                {
                    byte b = nal[i];
                    if (zeros >= 2 && b == 3)
                    {
                        zeros = 0;
                        continue;
                    }

                    payload.Add(b);
                    zeros = b == 0 ? zeros + 1 : 0;
                }
                data = payload.ToArray();
            }

            public long U(int count)
            {
                long value = 0;
                for (int i = 0; i < count; i++)
                {
                    if ((position >> 3) >= data.Length)
                        throw new EndOfStreamException();

                    int bit = (data[position >> 3] >> (7 - (position & 7))) & 1;
                    value = (value << 1) | (long)bit;
                    position++;
                }
                return value;
            }

            public int Ue()
            {
                int zeros = 0;
                while (U(1) == 0)
                {
                    zeros++;
                    if (zeros > 31)
                        throw new InvalidDataException();
                }
                return (int)((1L << zeros) - 1 + U(zeros));
            }

            public int Se()
            {
                int k = Ue();
                return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2);
            }
        }
    }
}
