#if !MAC

using Composition;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace Sound
{
    public class SpeechManager : IDisposable
    {

        private WorkQueue ttsQueue;

        private ISpeaker speaker;

        private bool mute;
        public bool Muted
        {
            get
            {
                return mute;
            }
            set
            {
                mute = value;
                if (mute)
                {
                    StopSpeech();
                }
            }
        }

        private PlatformTools platformTools;

        public string Voice { get; private set; }

        public int Volume { get; set; }

        public event Action<string> OnSpeech;

        public SpeechManager(PlatformTools platformTools, string voice, int volume)
        {
            Voice = voice;
            this.platformTools = platformTools;
            speaker = platformTools.CreateSpeaker(voice);

            if (speaker != null)
            {
                ttsQueue = new WorkQueue("Speecher TTS");
                Muted = false;
            }
            else
            {
                mute = true;
            }

            Volume = volume;
        }

        public void Dispose()
        {
            DisposeSpeech();
            ttsQueue?.Dispose();
            ttsQueue = null;
        }

        private void DisposeSpeech()
        {
            StopSpeech();

            // Not locking on the speaker, which the speech thread can replace (see ResetSpeaker).
            ISpeaker current = speaker;
            speaker = null;
            current?.Dispose();
        }
        public bool HasSpeech()
        {
            return ttsQueue != null;
        }

        public bool IsSpeaking
        {
            get { return ttsQueue?.NeedWorkDone ?? false; }
        }

        public void StopSpeech()
        {
            ttsQueue?.Clear();

            // The speaker can be replaced on the speech thread (see ResetSpeaker).
            ISpeaker current = speaker;
            try
            {
                current?.Stop();
            }
            catch (Exception e)
            {
                Logger.SoundLog.LogException(this, e);
            }
        }

        public void EnqueueSpeech(SpeechRequest speech)
        {
            if (ttsQueue == null)
            {
                speech.OnFinish?.Invoke();
                return;
            }

            if (Muted)
            {
                if (speech.OnFinish != null)
                {
                    ttsQueue.Enqueue(speech.OnFinish);
                }
            }
            else
            {
                SoundWorkItem soundWorkItem = new SoundWorkItem()
                {
                    Action = () => { Speak(speech); },
                    Priority = speech.Priority
                };
                ttsQueue.Enqueue(soundWorkItem);
            }
        }

        private void Speak(SpeechRequest request)
        {
            if (speaker == null)
            {
                speaker = platformTools.CreateSpeaker(Voice);
            }

            ISpeaker current = speaker;
            if (current == null)
                return;

            try
            {
                current.SetRate(Math.Max(-10, Math.Min(10, request.Rate)));

                float factorVolume = (request.Volume / 100.0f) * (Volume / 100.0f);

                current.SetVolume(Math.Clamp((int)(factorVolume * 100), 0, 100));
            }
            catch (Exception ex)
            {
                Logger.SoundLog.LogException(this, ex);
                ResetSpeaker(current);
                return;
            }

            SoundWorkItem[] soundWorkItems = ttsQueue.WorkItems.OfType<SoundWorkItem>().ToArray();

            int maxPriority = 0;
            if (soundWorkItems.Any())
            {
                maxPriority = soundWorkItems.Select(r => r.Priority).Max();
            }

            if (request.Expiry > DateTime.Now && maxPriority <= request.Priority)
            {
                try
                {
                    string text = SpeechParameters.CreateTextToSpeech(request.RawText, request.Parameters);
                    string displayText = SpeechParameters.CreateDisplayText(request.RawText, request.Parameters);
                    Logger.SoundLog.Log(this, "TTS", text, Logger.LogType.Notice);
                    if (!Muted)
                    {
                        if (!string.IsNullOrEmpty(text))
                        {
                            OnSpeech?.Invoke(displayText);
                            current.Speak(text);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.SoundLog.LogException(this, ex);
                    ResetSpeaker(current);
                }
                request.OnFinish?.Invoke();
            }
        }

        // A speaker that's failed, e.g. because its audio device went away, keeps failing, so the next speech makes a new one.
        private void ResetSpeaker(ISpeaker failed)
        {
            if (speaker != failed)
                return;

            speaker = null;
            try
            {
                failed.Dispose();
            }
            catch (Exception e)
            {
                Logger.SoundLog.LogException(this, e);
            }
        }
    }

    public class SpeechRequest : SoundRequest
    {
        public int Rate { get; set; }

        public string RawText { get; set; }

        public SpeechParameters Parameters { get; private set; }

        public SpeechRequest(string rawText, int rate, int volume, SpeechParameters parameters, DateTime expiry,Action onFinish)
             : base(parameters.Priority, volume, expiry, onFinish)
        {
            Rate = rate;
            RawText = rawText;
            Parameters = parameters;
        }
    }
}
#endif