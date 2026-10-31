using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tools;

namespace ImageServer
{
    // A recording's MP4 only gets its index (the moov box) when recording stops, so if the app's closed or crashes while
    // recording, all the video's in the file but it can't be played. This writes the missing index from what's there:
    // each frame's size, and whether it's a key frame, from the H.264 in the mdat box; when each was, from the recording
    // info's frame times; and the H.264 parameter sets, which the stream doesn't have. The recorder saves those alongside
    // (see GetParameterSetsFilename), or failing that they're taken from another recording they fit.
    public static class Mp4Repair
    {
        // Anything newer could still be being written.
        private static readonly TimeSpan minimumAge = TimeSpan.FromSeconds(30);

        private const int timescale = 90000;

        private const int maxOtherRecordings = 100;

        public static string GetParameterSetsFilename(string mp4Filename)
        {
            return Path.ChangeExtension(mp4Filename, ".parametersets.h264");
        }

        // True if it was unfinished and is now repaired.
        public static bool RepairIfUnfinished(string filename, IEnumerable<FrameTime> frameTimes)
        {
            try
            {
                FileInfo file = new FileInfo(filename);
                if (!file.Exists || !string.Equals(file.Extension, ".mp4", StringComparison.OrdinalIgnoreCase))
                    return false;

                using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
                {
                    Box mdat;
                    if (!IsUnfinished(stream, out mdat))
                        return false;
                }

                if (DateTime.UtcNow - file.LastWriteTimeUtc < minimumAge)
                {
                    Logger.VideoLog.LogStatic("Unfinished recording may still be being written, not repairing", filename);
                    return false;
                }

                return Repair(file, frameTimes);
            }
            catch (Exception e)
            {
                Logger.VideoLog.LogException("Repairing " + filename, e);
                return false;
            }
        }

        private static bool Repair(FileInfo file, IEnumerable<FrameTime> frameTimes)
        {
            // Just after the last frame was, which is put back afterwards.
            DateTime lastWriteUtc = file.LastWriteTimeUtc;

            if (!RepairFile(file, frameTimes, lastWriteUtc))
                return false;

            try
            {
                File.SetLastWriteTimeUtc(file.FullName, lastWriteUtc);
            }
            catch (Exception e)
            {
                Logger.VideoLog.LogException("Repairing " + file.FullName, e);
            }
            return true;
        }

        private static bool RepairFile(FileInfo file, IEnumerable<FrameTime> frameTimes, DateTime lastWriteUtc)
        {
            // Not shared for writing, so it can't be repaired while something's still writing it.
            using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1))
            {
                Box mdat;
                if (!IsUnfinished(stream, out mdat))
                    return false;

                long payloadStart = FindPayloadStart(stream, mdat);
                if (payloadStart < 0)
                {
                    Logger.VideoLog.LogStatic("Unfinished recording has no H.264 to repair it from", file.FullName);
                    return false;
                }

                Frames frames = ReadFrames(stream, payloadStart);
                if (!frames.Sizes.Any())
                {
                    Logger.VideoLog.LogStatic("Unfinished recording has no frames", file.FullName);
                    return false;
                }

                string source;
                H264Parameters parameters = FindParameters(file, frames, out source);
                if (parameters == null)
                {
                    Logger.VideoLog.LogStatic("Unfinished recording can't be repaired: no H.264 parameter sets that fit it", file.FullName);
                    return false;
                }

                uint[] durations = GetDurations(frames.Sizes.Count, frameTimes, parameters.FrameRate, lastWriteUtc);
                byte[] moov = CreateMoov(frames, parameters, durations, payloadStart);

                // If this is interrupted the file's still unfinished, and it's repaired next time: the index goes after
                // the last whole frame, and the mdat box only gets its size once it's written.
                stream.SetLength(frames.End);
                stream.Position = frames.End;
                stream.Write(moov, 0, moov.Length);
                stream.Flush(true);

                WriteMdatHeader(stream, mdat.Position, payloadStart, frames.End);
                stream.Flush(true);

                TimeSpan length = TimeSpan.FromSeconds(durations.Sum(d => (double)d) / timescale);
                Logger.VideoLog.LogStatic("Repaired unfinished recording", file.FullName, frames.Sizes.Count + " frames", length, parameters.Width + "x" + parameters.Height, "parameter sets from " + source);
                return true;
            }
        }

        private struct Box
        {
            public string Type;
            public long Position;
            public long Size;
            public int HeaderSize;

            public long End { get { return Position + Size; } }
        }

        private static bool ReadBox(FileStream stream, long position, long end, out Box box)
        {
            box = new Box();
            if (position + 8 > end)
                return false;

            byte[] header = ReadBytes(stream, position, (int)Math.Min(16, end - position));
            long size = ReadUInt32(header, 0);
            int headerSize = 8;
            if (size == 1)
            {
                if (header.Length < 16)
                    return false;

                size = (long)ReadUInt64(header, 8);
                headerSize = 16;
            }

            // An unknown size runs to the end, as an unfinished mdat box's does.
            if (size == 0)
                size = end - position;

            if (size < headerSize)
                return false;

            box.Type = Encoding.ASCII.GetString(header, 4, 4);
            box.Position = position;
            box.Size = size;
            box.HeaderSize = headerSize;
            return true;
        }

        // An mdat box that runs to the end of the file, and no moov box.
        private static bool IsUnfinished(FileStream stream, out Box mdat)
        {
            mdat = new Box();
            long length = stream.Length;
            bool hasMdat = false;

            long position = 0;
            Box box;
            while (ReadBox(stream, position, length, out box))
            {
                if (box.Type == "moov")
                    return false;

                if (box.Type == "mdat")
                {
                    mdat = box;
                    hasMdat = true;
                }

                if (box.End >= length)
                    break;

                position = box.End;
            }

            return hasMdat && mdat.End >= length;
        }

        // The recorder leaves room for a 64 bit size in the mdat box's header, so its contents start 8 bytes later than
        // its unfinished header says.
        private static long FindPayloadStart(FileStream stream, Box mdat)
        {
            foreach (long start in new long[] { mdat.Position + mdat.HeaderSize, mdat.Position + 16 }.Distinct())
            {
                if (StartsWithNals(stream, start, 3))
                    return start;
            }
            return -1;
        }

        private static bool StartsWithNals(FileStream stream, long position, int count)
        {
            long length = stream.Length;
            for (int i = 0; i < count && position < length; i++)
            {
                if (position + 5 > length)
                    return false;

                byte[] head = ReadBytes(stream, position, 5);
                long nalLength = ReadUInt32(head, 0);
                if (nalLength < 1 || position + 4 + nalLength > length || !IsNalHeader(head[4]))
                    return false;

                position += 4 + nalLength;
            }
            return true;
        }

        private static bool IsNalHeader(byte header)
        {
            int type = header & 0x1F;
            return (header & 0x80) == 0 && type >= 1 && type <= 12;
        }

        private class Frames
        {
            public List<int> Sizes = new List<int>();

            // Numbered from 1, as stss has them.
            public List<int> KeyFrames = new List<int>();

            // Just after the last whole frame.
            public long End;

            // If the stream has them.
            public byte[] Sps;
            public byte[] Pps;

            // The first IDR picture's first slice, enough of it to check parameter sets against.
            public byte[] IdrSlice;
        }

        // The frames are length prefixed NAL units, one after another. Each starts with an access unit delimiter, or
        // failing that, with the first of its NAL units before its first slice.
        private static Frames ReadFrames(FileStream stream, long start)
        {
            Frames frames = new Frames() { End = start };
            long length = stream.Length;

            long frameSize = 0;
            bool frameHasSlice = false;
            bool frameIsKey = false;

            long position = start;
            while (position + 5 <= length)
            {
                byte[] head = ReadBytes(stream, position, (int)Math.Min(8, length - position));
                long nalLength = ReadUInt32(head, 0);

                // Cut off part way through, as the last one can be, or not H.264 any more (e.g. the index from an
                // interrupted repair).
                if (nalLength < 1 || position + 4 + nalLength > length || !IsNalHeader(head[4]))
                    break;

                int type = head[4] & 0x1F;
                bool slice = type == 1 || type == 5;

                // first_mb_in_slice is 0.
                bool firstSlice = slice && nalLength > 1 && head.Length > 5 && (head[5] & 0x80) != 0;

                if (frameHasSlice && (type == 9 || type == 6 || type == 7 || type == 8 || firstSlice))
                {
                    AddFrame(frames, frameSize, frameIsKey, position);
                    frameSize = 0;
                    frameHasSlice = false;
                    frameIsKey = false;
                }

                if (type == 7 && frames.Sps == null && nalLength < 1024)
                    frames.Sps = ReadBytes(stream, position + 4, (int)nalLength);

                if (type == 8 && frames.Pps == null && nalLength < 1024)
                    frames.Pps = ReadBytes(stream, position + 4, (int)nalLength);

                if (type == 5 && firstSlice && frames.IdrSlice == null)
                    frames.IdrSlice = ReadBytes(stream, position + 4, (int)Math.Min(nalLength, 256));

                frameSize += 4 + nalLength;
                frameHasSlice |= slice;
                frameIsKey |= type == 5;
                position += 4 + nalLength;
            }

            if (frameHasSlice)
            {
                AddFrame(frames, frameSize, frameIsKey, position);
            }
            return frames;
        }

        private static void AddFrame(Frames frames, long size, bool key, long end)
        {
            frames.Sizes.Add((int)size);
            if (key)
                frames.KeyFrames.Add(frames.Sizes.Count);

            frames.End = end;
        }

        private static H264Parameters FindParameters(FileInfo file, Frames frames, out string source)
        {
            // In the stream, e.g. from a camera that sends H.264.
            H264Parameters parameters = H264Parameters.FromNals(frames.Sps, frames.Pps);
            if (Fits(parameters, frames))
            {
                source = "the recording";
                return parameters;
            }

            // Saved by the recorder.
            string saved = GetParameterSetsFilename(file.FullName);
            if (File.Exists(saved))
            {
                parameters = H264Parameters.FromAnnexB(File.ReadAllBytes(saved));
                if (Fits(parameters, frames))
                {
                    source = Path.GetFileName(saved);
                    return parameters;
                }
            }

            // Another recording made the same way, most likely the nearest in time from the same video source.
            foreach (FileInfo other in GetOtherRecordings(file).Take(maxOtherRecordings))
            {
                byte[] avcC = ReadAvcC(other.FullName);
                parameters = avcC != null ? H264Parameters.FromAvcC(avcC) : null;
                if (Fits(parameters, frames))
                {
                    source = other.FullName;
                    return parameters;
                }
            }

            source = null;
            return null;
        }

        private static bool Fits(H264Parameters parameters, Frames frames)
        {
            if (parameters == null)
                return false;

            return frames.IdrSlice == null || parameters.FitsIdrSlice(frames.IdrSlice);
        }

        // Recordings are in the event's directory, a directory for each race, named after their video source.
        private static IEnumerable<FileInfo> GetOtherRecordings(FileInfo file)
        {
            DirectoryInfo eventDirectory = file.Directory?.Parent;
            if (eventDirectory == null)
                yield break;

            DateTime recorded = file.CreationTimeUtc;
            Func<FileInfo, double> distance = (f) => Math.Abs((f.LastWriteTimeUtc - recorded).TotalSeconds);

            FileInfo[] sameEvent = GetRecordings(eventDirectory).Where(f => !string.Equals(f.FullName, file.FullName, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (FileInfo other in sameEvent.Where(f => f.Name == file.Name).OrderBy(distance))
                yield return other;

            foreach (FileInfo other in sameEvent.Where(f => f.Name != file.Name).OrderBy(distance))
                yield return other;

            DirectoryInfo eventsDirectory = eventDirectory.Parent;
            if (eventsDirectory == null)
                yield break;

            DirectoryInfo[] otherEvents;
            try
            {
                otherEvents = eventsDirectory.GetDirectories().Where(d => d.FullName != eventDirectory.FullName).ToArray();
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (FileInfo other in otherEvents.SelectMany(GetRecordings).OrderBy(distance))
                yield return other;
        }

        private static IEnumerable<FileInfo> GetRecordings(DirectoryInfo eventDirectory)
        {
            try
            {
                return eventDirectory.GetDirectories().SelectMany(d => d.GetFiles("*.mp4")).ToArray();
            }
            catch (Exception)
            {
                return new FileInfo[0];
            }
        }

        private static byte[] ReadAvcC(string filename)
        {
            try
            {
                using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
                {
                    return FindAvcC(stream, 0, stream.Length);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // In moov/trak/mdia/minf/stbl/stsd/avc1.
        private static byte[] FindAvcC(FileStream stream, long start, long end)
        {
            long position = start;
            Box box;
            while (ReadBox(stream, position, end, out box))
            {
                long contents = box.Position + box.HeaderSize;
                byte[] found = null;
                switch (box.Type)
                {
                    case "moov":
                    case "trak":
                    case "mdia":
                    case "minf":
                    case "stbl":
                        found = FindAvcC(stream, contents, box.End);
                        break;

                    // After its version, flags and entry count.
                    case "stsd":
                        found = FindAvcC(stream, contents + 8, box.End);
                        break;

                    // After the visual sample entry's fields.
                    case "avc1":
                    case "avc3":
                        found = FindAvcC(stream, contents + 78, box.End);
                        break;

                    case "avcC":
                        if (box.Size < 64 * 1024)
                            found = ReadBytes(stream, contents, (int)(box.End - contents));
                        break;
                }

                if (found != null)
                    return found;

                if (box.End >= end)
                    break;

                position = box.End;
            }
            return null;
        }

        // Each frame's duration in timescale units, so the replay lines up with the race as it would have. The recording
        // info has when a frame was every so often, numbered as the recorder was given them, but the encoder can drop
        // some, so then they're not quite the frames in the file. That shows in when the file was last written, just
        // after its last frame: if it's later than its frames would take, the times are spread over the frames there are.
        private static uint[] GetDurations(int count, IEnumerable<FrameTime> frameTimes, double frameRate, DateTime lastWriteUtc)
        {
            List<FrameTime> received = new List<FrameTime>();
            if (frameTimes != null)
            {
                foreach (FrameTime frameTime in frameTimes.OrderBy(f => f.Frame))
                {
                    bool after = !received.Any() || (frameTime.Frame > received.Last().Frame && frameTime.Seconds > received.Last().Seconds);
                    if (frameTime.Frame >= 1 && frameTime.Seconds >= 0 && after)
                    {
                        received.Add(frameTime);
                    }
                }
            }

            if (!received.Any())
            {
                received.Add(new FrameTime() { Frame = 1, Seconds = 0 });
            }

            FrameTime first = received.First();
            FrameTime last = received.Last();

            double receivedRate = frameRate > 1 && frameRate < 1000 ? frameRate : 30;
            if (last.Frame - first.Frame >= 10)
            {
                receivedRate = (last.Frame - first.Frame) / (last.Seconds - first.Seconds);
            }

            // When the last frame would be if none were dropped.
            double end = last.Seconds + Math.Max(0, count - last.Frame) / receivedRate;

            double ratio = 1;
            if (last.Time != default(DateTime))
            {
                double written = last.Seconds + (lastWriteUtc - last.Time.ToUniversalTime()).TotalSeconds;
                double receivedByThen = last.Frame + (written - last.Seconds) * receivedRate;

                // Not if it's a lot, which would more likely be the file being changed since.
                double writtenRatio = count / receivedByThen;
                if (written - end > 0.5 && writtenRatio >= 0.5)
                {
                    ratio = writtenRatio;
                    end = written;
                }
            }

            // Frames numbered from 0, and their times. The first is at 0 by definition.
            List<KeyValuePair<int, double>> known = new List<KeyValuePair<int, double>>() { new KeyValuePair<int, double>(0, 0) };
            foreach (FrameTime frameTime in received)
            {
                int index = (int)Math.Round((frameTime.Frame - 1) * ratio);
                KeyValuePair<int, double> previous = known.Last();
                if (index > previous.Key && index < count && frameTime.Seconds > previous.Value)
                {
                    known.Add(new KeyValuePair<int, double>(index, frameTime.Seconds));
                }
            }

            if (ratio < 1 && count - 1 > known.Last().Key && end > known.Last().Value)
            {
                known.Add(new KeyValuePair<int, double>(count - 1, end));
            }

            uint nominal = (uint)Math.Max(1, Math.Round(timescale / (receivedRate * ratio)));

            uint[] durations = new uint[count];
            for (int k = 0; k < known.Count; k++)
            {
                int from = known[k].Key;
                if (k + 1 < known.Count)
                {
                    int to = known[k + 1].Key;
                    int frames = to - from;
                    long total = (long)Math.Round(known[k + 1].Value * timescale) - (long)Math.Round(known[k].Value * timescale);
                    long each = Math.Max(1, total / frames);
                    for (int i = from; i < to; i++)
                    {
                        durations[i] = (uint)each;
                    }

                    // So the next known frame's exactly on time.
                    durations[to - 1] = (uint)Math.Max(1, total - each * (frames - 1));
                }
                else
                {
                    for (int i = from; i < count; i++)
                    {
                        durations[i] = nominal;
                    }
                }
            }
            return durations;
        }

        // One video track, its frames all in one chunk at chunkOffset, as the recorder writes them.
        private static byte[] CreateMoov(Frames frames, H264Parameters parameters, uint[] durations, long chunkOffset)
        {
            ulong mediaDuration = 0;
            foreach (uint duration in durations)
            {
                mediaDuration += duration;
            }
            ulong movieDuration = mediaDuration * 1000 / timescale;
            int version = mediaDuration > uint.MaxValue ? 1 : 0;

            BoxWriter w = new BoxWriter();
            w.Begin("moov");
            {
                w.Begin("mvhd", version, 0);
                w.Times(version);
                w.U32(1000);
                w.Duration(version, movieDuration);
                w.U32(0x00010000); // rate
                w.U16(0x0100); // volume
                w.Zeros(10);
                w.Matrix();
                w.Zeros(24);
                w.U32(2); // next track id
                w.End();

                w.Begin("trak");
                {
                    w.Begin("tkhd", version, 3); // enabled, in movie
                    w.Times(version);
                    w.U32(1); // track id
                    w.U32(0);
                    w.Duration(version, movieDuration);
                    w.Zeros(8);
                    w.U16(0); // layer
                    w.U16(0); // alternate group
                    w.U16(0); // volume
                    w.U16(0);
                    w.Matrix();
                    w.U32((uint)parameters.Width << 16);
                    w.U32((uint)parameters.Height << 16);
                    w.End();

                    w.Begin("mdia");
                    {
                        w.Begin("mdhd", version, 0);
                        w.Times(version);
                        w.U32(timescale);
                        w.Duration(version, mediaDuration);
                        w.U16(0x55C4); // und
                        w.U16(0);
                        w.End();

                        w.Begin("hdlr", 0, 0);
                        w.U32(0);
                        w.Type("vide");
                        w.Zeros(12);
                        w.Bytes(Encoding.ASCII.GetBytes("VideoHandler\0"));
                        w.End();

                        w.Begin("minf");
                        {
                            w.Begin("vmhd", 0, 1);
                            w.Zeros(8);
                            w.End();

                            w.Begin("dinf");
                            w.Begin("dref", 0, 0);
                            w.U32(1);
                            w.Begin("url ", 0, 1); // in this file
                            w.End();
                            w.End();
                            w.End();

                            w.Begin("stbl");
                            WriteSampleTable(w, frames, parameters, durations, chunkOffset);
                            w.End();
                        }
                        w.End();
                    }
                    w.End();
                }
                w.End();
            }
            w.End();
            return w.ToArray();
        }

        private static void WriteSampleTable(BoxWriter w, Frames frames, H264Parameters parameters, uint[] durations, long chunkOffset)
        {
            w.Begin("stsd", 0, 0);
            w.U32(1);
            {
                w.Begin("avc1");
                w.Zeros(6);
                w.U16(1); // data reference index
                w.Zeros(16);
                w.U16(parameters.Width);
                w.U16(parameters.Height);
                w.U32(0x00480000); // 72 dpi
                w.U32(0x00480000);
                w.U32(0);
                w.U16(1); // frames per sample
                w.Zeros(32); // compressor name
                w.U16(0x0018); // depth
                w.U16(0xFFFF);

                w.Begin("avcC");
                w.Bytes(parameters.ToAvcC());
                w.End();
                w.End();
            }
            w.End();

            // Runs of the same duration.
            List<KeyValuePair<uint, uint>> runs = new List<KeyValuePair<uint, uint>>();
            foreach (uint duration in durations)
            {
                if (runs.Any() && runs.Last().Value == duration)
                {
                    runs[runs.Count - 1] = new KeyValuePair<uint, uint>(runs.Last().Key + 1, duration);
                }
                else
                {
                    runs.Add(new KeyValuePair<uint, uint>(1, duration));
                }
            }

            w.Begin("stts", 0, 0);
            w.U32((uint)runs.Count);
            foreach (KeyValuePair<uint, uint> run in runs)
            {
                w.U32(run.Key);
                w.U32(run.Value);
            }
            w.End();

            // Without it every frame's a key frame.
            if (frames.KeyFrames.Count < frames.Sizes.Count)
            {
                w.Begin("stss", 0, 0);
                w.U32((uint)frames.KeyFrames.Count);
                foreach (int keyFrame in frames.KeyFrames)
                {
                    w.U32((uint)keyFrame);
                }
                w.End();
            }

            w.Begin("stsc", 0, 0);
            w.U32(1);
            w.U32(1); // first chunk
            w.U32((uint)frames.Sizes.Count);
            w.U32(1); // sample description
            w.End();

            w.Begin("stsz", 0, 0);
            w.U32(0); // sizes vary
            w.U32((uint)frames.Sizes.Count);
            foreach (int size in frames.Sizes)
            {
                w.U32((uint)size);
            }
            w.End();

            if (chunkOffset <= uint.MaxValue)
            {
                w.Begin("stco", 0, 0);
                w.U32(1);
                w.U32((uint)chunkOffset);
                w.End();
            }
            else
            {
                w.Begin("co64", 0, 0);
                w.U32(1);
                w.U64((ulong)chunkOffset);
                w.End();
            }
        }

        private static void WriteMdatHeader(FileStream stream, long mdatPosition, long payloadStart, long end)
        {
            long size = end - mdatPosition;

            BoxWriter w = new BoxWriter();
            if (payloadStart - mdatPosition == 16)
            {
                w.U32(1);
                w.Type("mdat");
                w.U64((ulong)size);
            }
            else
            {
                if (size > uint.MaxValue)
                    throw new IOException("Too big for its mdat box's header");

                w.U32((uint)size);
                w.Type("mdat");
            }

            byte[] header = w.ToArray();
            stream.Position = mdatPosition;
            stream.Write(header, 0, header.Length);
        }

        private static byte[] ReadBytes(FileStream stream, long position, int count)
        {
            byte[] bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int got = RandomAccess.Read(stream.SafeFileHandle, new Span<byte>(bytes, read, count - read), position + read);
                if (got <= 0)
                    throw new EndOfStreamException();
                read += got;
            }
            return bytes;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
        }

        private static ulong ReadUInt64(byte[] data, int offset)
        {
            return (ulong)ReadUInt32(data, offset) << 32 | ReadUInt32(data, offset + 4);
        }

        private class BoxWriter
        {
            private MemoryStream stream = new MemoryStream();
            private Stack<long> starts = new Stack<long>();

            public void Begin(string type)
            {
                starts.Push(stream.Position);
                U32(0);
                Type(type);
            }

            public void Begin(string type, int version, int flags)
            {
                Begin(type);
                U8(version);
                U8(flags >> 16);
                U16(flags);
            }

            public void End()
            {
                long start = starts.Pop();
                long end = stream.Position;
                stream.Position = start;
                U32((uint)(end - start));
                stream.Position = end;
            }

            public void Type(string type)
            {
                Bytes(Encoding.ASCII.GetBytes(type));
            }

            public void U8(int value)
            {
                stream.WriteByte((byte)value);
            }

            public void U16(int value)
            {
                U8(value >> 8);
                U8(value);
            }

            public void U32(uint value)
            {
                U8((int)(value >> 24));
                U8((int)(value >> 16));
                U8((int)(value >> 8));
                U8((int)value);
            }

            public void U64(ulong value)
            {
                U32((uint)(value >> 32));
                U32((uint)value);
            }

            public void Zeros(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    U8(0);
                }
            }

            public void Bytes(byte[] bytes)
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            // Creation and modification times, unknown.
            public void Times(int version)
            {
                Zeros(version == 1 ? 16 : 8);
            }

            public void Duration(int version, ulong duration)
            {
                if (version == 1)
                    U64(duration);
                else
                    U32((uint)duration);
            }

            public void Matrix()
            {
                U32(0x00010000); U32(0); U32(0);
                U32(0); U32(0x00010000); U32(0);
                U32(0); U32(0); U32(0x40000000);
            }

            public byte[] ToArray()
            {
                return stream.ToArray();
            }
        }
    }
}
