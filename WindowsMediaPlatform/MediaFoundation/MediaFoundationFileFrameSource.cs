using ImageServer;
using MediaFoundation;
using MediaFoundation.Misc;
using MediaFoundation.ReadWrite;
using MediaFoundation.Transform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tools;

namespace WindowsMediaPlatform.MediaFoundation
{
    public class MediaFoundationFileFrameSource : MediaFoundationFrameSource, IPlaybackFrameSource
    {
        private IMFMediaSource source;
        private IMFPresentationDescriptor presentationDescriptor;

        public FileInfo Path { get; private set; }
        public FrameTime[] FrameTimes { get; private set; }

        public PlaybackSpeed PlaybackSpeed { get; set; }

        public TimeSpan MediaTime
        {
            get
            {
                if (seekingFrame.HasValue)
                {
                    return TimeSpan.FromSeconds(seekingFrame.Value / FrameRate);
                }

                long? shown = cachedShown;
                if (shown.HasValue)
                {
                    return GetFrameTime(shown.Value);
                }

                return sampleTime;
            }
        }

        public DateTime StartTime
        {
            get
            {
                return FrameTimes.GetRealTime(TimeSpan.Zero, Latency);
            }
        }

        public DateTime CurrentTime
        {
            get
            {
                return FrameTimes.GetRealTime(MediaTime, Latency);
            }
        }

        public double FrameRate { get; set; }

        public override bool Connected 
        { 
            get
            {
                return source != null;
            }
            protected set
            {
                base.Connected = value;
            }
        }

        private TimeSpan sampleTime;
        private long sampleFrame;

        public TimeSpan Length { get; private set; }


        private object seekLock;

        private SeekRequest seekRequest;
        private long? seekingFrame;

        // Frames to decode and show without seeking. The reader is already sitting on the next
        // sample, so stepping forward is one decode instead of a seek back to the keyframe.
        private int stepForward;

        // Back-step cache. Stepping back has to seek to the keyframe and decode forward to the
        // target, so it already decodes the frames just before the target; keeping the last few
        // means the next steps back are shown from memory instead of seeking again. Bounded by
        // bytes rather than frames, since an RGB32 720p frame is 3.6MB.
        private const long BackCacheBudgetBytes = 96L * 1024 * 1024;
        private readonly Dictionary<long, CachedFrame> backCache = new Dictionary<long, CachedFrame>();
        private readonly Stack<byte[]> spareFrameBuffers = new Stack<byte[]>();
        private long backCacheFirst = long.MaxValue;
        private long backCacheLast = long.MinValue;

        // A frame on screen from the cache, when that isn't where the decoder is sitting.
        private long? cachedShown;
        // A cached frame the decode thread should put on screen next.
        private long? pendingCachedShow;

        // Wakes the decode thread when it's idling while paused, so a step or seek doesn't wait
        // out the idle poll (a Windows timer tick, ~15.6ms) before anything happens.
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private DateTime epoch;

        public TimeSpan Latency { get; private set; }
        public bool Repeat { get; set; }

        public bool IsAtEnd
        {
            get
            {
                return MediaTime >= Length;
            }
        }

        public TimeSpan FrameTime
        {
            get
            {
                return TimeSpan.FromMilliseconds(1000 / FrameRate);
            }
        }

        public MediaFoundationFileFrameSource(VideoConfig videoConfig) 
            : base(videoConfig)
        {
            seekLock = new object();

            ASync = false;
            AutomaticVideoConversion = true;

            //Tools.Logger.VideoLog.LogCall(this, videoConfig.FilePath);
            Path = new FileInfo(videoConfig.FilePath);
            FrameTimes = videoConfig.FrameTimes;
            PlaybackSpeed = PlaybackSpeed.Normal;


            Latency = TimeSpan.Zero;
            if (VideoConfig.DeviceLatency != 0)
            {
                Latency = TimeSpan.FromSeconds(VideoConfig.DeviceLatency);
            }
        }

        protected override void ProcessImage()
        {
            if (ShowPendingCachedFrame())
            {
                return;
            }

            if (State == States.Paused && seekingFrame == null && stepForward == 0)
            {
                wake.WaitOne(10);
            }
            else
            {
                IMFSample sample;
                HResult hr = Read(out sample);
                if (MFHelper.Succeeded(hr) && sample != null)
                {
                    if (PlaybackSpeed == PlaybackSpeed.Slow && FrameRate > 0)
                    {
                        int rate = 4;
                        epoch += FrameTime * rate;
                    }

                    DateTime due = epoch + sample.GetSampleTime();

                    sampleTime = sample.GetSampleTime();
                    sampleFrame = GetFrameTime(sampleTime);

                    //Logger.VideoLog.Log(this, sampleFrame + ", tim " + sample.GetSampleTime() + ", dur " + sample.GetSampleDuration());
                    CurrentlySeeking();

                    lock (seekLock)
                    {
                        if (seekingFrame == null)
                        {
                            cachedShown = null;

                            if (stepForward > 0)
                            {
                                stepForward--;
                            }
                        }
                    }

                    if (PlaybackSpeed != PlaybackSpeed.FastAsPossible && State == States.Running && (seekingFrame == null || seekingFrame.Value < sampleFrame))
                    {
                        TimeSpan diff = due - DateTime.Now;

                        if (diff > TimeSpan.Zero)
                        {
                            Thread.Sleep(diff);
                        }
                    }

                    if (AutomaticVideoConversion)
                    {
                        ProcessRGBSample(sample);
                    }
                    else
                    {
                        ProcessRaw(sample);
                    }

                    Connected = true;
                    base.ProcessImage();
                    MFHelper.SafeRelease(sample);
                }
                else
                {
                    // Nothing left to read, so a pending forward step can never complete.
                    lock (seekLock)
                    {
                        stepForward = 0;
                    }

                    if (sampleFrame > 0 && State == States.Running)
                    {
                        if (Repeat)
                        {
                            SetPosition(TimeSpan.Zero);
                        }
                    }
                }
            }

            lock (seekLock)
            {
                SeekRequest request = seekRequest;
                seekRequest = null;

                if (request != null)
                {
                    stepForward = 0;
                    cachedShown = null;
                    pendingCachedShow = null;

                    long? frame = ResolveFrame(request);

                    if (frame.HasValue)
                    {
                        if (frame < 0)
                            frame = 0;

                        ClearBackCache();
                        if (request.CacheBack)
                        {
                            backCacheLast = frame.Value;
                            backCacheFirst = frame.Value - BackCacheCapacity() + 1;
                        }

                        TimeSpan actualMediaTime = GetFrameTime(frame.Value);

                        reader.Flush(0);
                        using (PropVariant value = new PropVariant(actualMediaTime.Ticks))
                        {
                            reader.SetCurrentPosition(Guid.Empty, value);
                            seekingFrame = frame;
                            epoch = DateTime.Now - actualMediaTime;
                        }
                    }
                }
            }
        }

        protected override HResult ProcessRGBSample(IMFSample sample)
        {
            CacheSample(sample);

            if (!CurrentlySeeking())
            {
                return base.ProcessRGBSample(sample);
            }
            NotifyReceivedFrame();
            return HResult.S_OK;
        }


        public bool CurrentlySeeking()
        {
            lock (seekLock)
            {
                if (seekingFrame.HasValue)
                {
                    if (sampleFrame >= seekingFrame.Value)
                    {
                        //Logger.VideoLog.LogCall(this, sampleFrame, (int)(seek.Value / FrameRate), seek.Value % FrameRate);
                        seekingFrame = null;
                        return false;
                    }

                    return true;
                }
                return false;
            }
        }

        private void CreateMediaSource(string sURL)
        {
            IMFSourceResolver sourceResolver;
            object tempSource;

            // Create the source resolver.
            HResult hr = MFExtern.MFCreateSourceResolver(out sourceResolver);
            MFError.ThrowExceptionForHR(hr);

            try
            {
                // Use the source resolver to create the media source.
                MFObjectType ObjectType = MFObjectType.Invalid;

                hr = sourceResolver.CreateObjectFromURL(
                        sURL,                       // URL of the source.
                        MFResolution.MediaSource,   // Create a source object.
                        null,                       // Optional property store.
                        out ObjectType,             // Receives the created object type.
                        out tempSource                 // Receives a pointer to the media source.
                    );
                MFError.ThrowExceptionForHR(hr);

                // Get the IMFMediaSource interface from the media source.
                source = (IMFMediaSource)tempSource;

                hr = source.CreatePresentationDescriptor(out presentationDescriptor);
                MFError.ThrowExceptionForHR(hr);

                long ticks;
                hr = presentationDescriptor.GetUINT64(MFAttributesClsid.MF_PD_DURATION, out ticks);
                MFError.ThrowExceptionForHR(hr);

                Length = TimeSpan.FromTicks(ticks);

                CreateReader(source);
            }
            finally
            {
                // Clean up
                MFHelper.SafeRelease(sourceResolver);
            }
        }

        protected override HResult SetupTransforms(out IMFMediaType sourceMediaType, out IMFMediaType outputMediaType)
        {
            HResult hr = base.SetupTransforms(out sourceMediaType, out outputMediaType);

            int numerator;
            int denominator;

            MFExtern.MFGetAttribute2UINT32asUINT64(outputMediaType, MFAttributesClsid.MF_MT_FRAME_RATE, out numerator, out denominator);

            FrameRate = numerator / denominator;

            return hr;
        }

        public override void CleanUp()
        {
            lock (seekLock)
            {
                ClearBackCache();
                spareFrameBuffers.Clear();
                cachedShown = null;
                pendingCachedShow = null;
            }

            // Flush any in-progress ReadSample so imageProcessor exits cleanly before
            // base.CleanUp() releases the reader — otherwise SafeRelease races the read.
            reader?.Flush((int)MF_SOURCE_READER.AllStreams);

            base.CleanUp();

            if (source != null)
            {
                MFHelper.SafeRelease(source);
                source = null;
            }
        }

        public override bool Start()
        {
            if (!Path.Exists)
                return false;

            if (source == null)
            {
                CreateMediaSource(Path.FullName);
                NotifyReceivedFrame();
            }

            epoch = DateTime.Now - MediaTime;

            return base.Start();
        }

        public override bool Pause()
        {
            return base.Pause();
        }

        public void Play()
        {
            lock (seekLock)
            {
                // The decoder is sitting past a frame shown from the cache, so start from the frame
                // on screen rather than jumping forward to where the decoder is.
                long? shown = pendingCachedShow ?? cachedShown;
                if (shown.HasValue && seekRequest == null)
                {
                    seekRequest = new SeekRequest() { Frame = shown.Value };
                }

                // The cache is only for stepping; playing through it would copy every frame. Let
                // the buffers go too, so the memory is only held while someone is stepping.
                ClearBackCache();
                spareFrameBuffers.Clear();
            }
            wake.Set();

            Unpause();
            epoch = DateTime.Now - MediaTime;
        }

        public void SetPosition(DateTime seekTime)
        {
            seekRequest = new SeekRequest() { DateTime = seekTime };
            wake.Set();
        }

        public void SetPosition(TimeSpan mediaTime)
        {
            seekRequest = new SeekRequest() { MediaTime = mediaTime };
            wake.Set();
        }

        public void SetPosition(long frame)
        {
            seekRequest = new SeekRequest() { Frame = frame };
            wake.Set();
        }

        public void Mute(bool mute = true)
        {
        }

        public override IEnumerable<Mode> GetModes()
        {
            yield break;
        }

        protected long GetFrameTime(TimeSpan mediaTime)
        {
            return (long)Math.Round(mediaTime.TotalSeconds * FrameRate);
        }

        protected TimeSpan GetFrameTime(long frameNumber)
        {
            return frameNumber * FrameTime;
        }

        public void PrevFrame()
        {
            StepFrames(-1);
        }
        public void NextFrame()
        {
            StepFrames(1);
        }

        // A seek lands on the keyframe before the target and decodes forward, updating
        // sampleFrame as it goes. Stepping from sampleFrame mid-seek therefore stepped from
        // somewhere near that keyframe and jumped the replay backwards. Step from the frame
        // we're heading to instead: a queued request, then a seek in progress, then the frame
        // on screen.
        private void StepFrames(int count)
        {
            lock (seekLock)
            {
                QueueStep(count);
            }

            wake.Set();
        }

        // Call with seekLock held.
        private void QueueStep(int count)
        {
            long? pending = seekRequest != null ? ResolveFrame(seekRequest) : null;

            long from;
            if (pending.HasValue)
            {
                from = pending.Value;
            }
            else if (seekingFrame.HasValue)
            {
                from = seekingFrame.Value;
            }
            else if (pendingCachedShow.HasValue)
            {
                from = pendingCachedShow.Value;
            }
            else if (cachedShown.HasValue)
            {
                from = cachedShown.Value;
            }
            else
            {
                from = sampleFrame + stepForward;
            }

            long target = Math.Max(0, from + count);

            bool settled = !pending.HasValue && !seekingFrame.HasValue;
            if (settled)
            {
                // Already decoded: put it straight on screen.
                if (stepForward == 0 && backCache.ContainsKey(target))
                {
                    pendingCachedShow = target;
                    return;
                }

                // Ahead of the decoder: just let the next samples through. A seek would throw
                // away the decoder's position and rebuild it from the keyframe.
                if (target > sampleFrame)
                {
                    // Keep what we step through, so stepping back over it again is free.
                    if (State == States.Paused && backCacheLast == long.MinValue)
                    {
                        backCacheLast = sampleFrame;
                        backCacheFirst = sampleFrame - BackCacheCapacity() + 1;
                    }

                    stepForward = (int)Math.Min(int.MaxValue, target - sampleFrame);
                    pendingCachedShow = null;
                    return;
                }
            }

            pendingCachedShow = null;
            seekRequest = new SeekRequest() { Frame = target, CacheBack = count < 0 };
        }

        private long? ResolveFrame(SeekRequest request)
        {
            TimeSpan? mediaTime = request.MediaTime;

            if (request.DateTime.HasValue)
            {
                mediaTime = FrameTimes.GetMediaTime(request.DateTime.Value, Latency);
            }

            if (mediaTime.HasValue)
            {
                return GetFrameTime(mediaTime.Value);
            }

            return request.Frame;
        }

        private int BackCacheCapacity()
        {
            long frameBytes = (long)Math.Max(1, FrameWidth) * Math.Max(1, FrameHeight) * 4;
            return (int)Math.Max(4, Math.Min(120, BackCacheBudgetBytes / frameBytes));
        }

        // Call with seekLock held. Keeps the buffers for reuse; they're large enough to land on the
        // large object heap, so reallocating them on every seek would churn it.
        private void ClearBackCache()
        {
            foreach (CachedFrame cachedFrame in backCache.Values)
            {
                spareFrameBuffers.Push(cachedFrame.Data);
            }
            backCache.Clear();

            backCacheFirst = long.MaxValue;
            backCacheLast = long.MinValue;
        }

        private void CacheSample(IMFSample sample)
        {
            lock (seekLock)
            {
                if (backCacheLast == long.MinValue)
                    return;

                long frame = sampleFrame;

                // Stepping forward off the end of the window: move it along with us.
                if (frame == backCacheLast + 1 && seekingFrame == null && State == States.Paused)
                {
                    long first = frame - BackCacheCapacity() + 1;
                    for (long old = backCacheFirst; old < first; old++)
                    {
                        CachedFrame dropped;
                        if (backCache.TryGetValue(old, out dropped))
                        {
                            spareFrameBuffers.Push(dropped.Data);
                            backCache.Remove(old);
                        }
                    }

                    backCacheFirst = first;
                    backCacheLast = frame;
                }

                if (frame < backCacheFirst || frame > backCacheLast || backCache.ContainsKey(frame))
                    return;

                int size = FrameWidth * FrameHeight * 4;
                if (size <= 0)
                    return;

                IMFMediaBuffer buffer = null;
                try
                {
                    if (MFHelper.Failed(sample.GetBufferByIndex(0, out buffer)))
                        return;

                    IntPtr data;
                    int maxLength;
                    int length;
                    if (MFHelper.Failed(buffer.Lock(out data, out maxLength, out length)))
                        return;

                    try
                    {
                        if (length < size)
                            return;

                        byte[] bytes = null;
                        while (spareFrameBuffers.Count > 0 && bytes == null)
                        {
                            byte[] spare = spareFrameBuffers.Pop();
                            if (spare.Length == size)
                            {
                                bytes = spare;
                            }
                        }

                        if (bytes == null)
                        {
                            bytes = new byte[size];
                        }

                        Marshal.Copy(data, bytes, 0, size);
                        backCache[frame] = new CachedFrame() { Data = bytes, SampleTime = sampleTime.Ticks };
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
                finally
                {
                    if (buffer != null)
                    {
                        MFHelper.SafeRelease(buffer);
                    }
                }
            }
        }

        // Puts a cached frame on screen through the same ring of raw textures a decoded sample
        // uses. Runs on the decode thread so it's the only writer to that ring. The ArUco overlay
        // hook is skipped: it only draws on live detection feeds, never on replay files.
        private bool ShowPendingCachedFrame()
        {
            lock (seekLock)
            {
                if (!pendingCachedShow.HasValue)
                    return false;

                long frame = pendingCachedShow.Value;
                pendingCachedShow = null;

                CachedFrame cachedFrame;
                if (!backCache.TryGetValue(frame, out cachedFrame))
                    return false;

                // Back on the decoder's own frame, the cache is no longer what's on screen.
                cachedShown = frame == sampleFrame ? (long?)null : frame;

                // Move the position on even if the frame can't be written. A hidden feed isn't
                // drawn, so its ring fills and stays full; it still has to keep in step with the
                // others, the same as when a decoded frame is dropped.
                XBuffer<RawTexture> currentRawTextures = rawTextures;
                RawTexture rawTexture;
                if (currentRawTextures != null && currentRawTextures.GetWritable(out rawTexture))
                {
                    if (cachedFrame.SampleTime != SampleTime)
                    {
                        SampleTime = cachedFrame.SampleTime;
                        FrameProcessNumber++;
                    }

                    GCHandle handle = GCHandle.Alloc(cachedFrame.Data, GCHandleType.Pinned);
                    try
                    {
                        rawTexture.SetData(handle.AddrOfPinnedObject(), cachedFrame.SampleTime, FrameProcessNumber);
                    }
                    finally
                    {
                        handle.Free();
                    }
                    currentRawTextures.WriteOne(rawTexture);
                }
            }

            OnFrame(SampleTime, FrameProcessNumber);
            return true;
        }

        private class SeekRequest
        {
            public DateTime? DateTime;
            public TimeSpan? MediaTime;
            public long? Frame;

            // A step backwards: keep the frames decoded on the way to the target.
            public bool CacheBack;
        }

        private class CachedFrame
        {
            public byte[] Data;
            public long SampleTime;
        }
    }
}
