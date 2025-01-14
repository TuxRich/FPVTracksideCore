﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Timing.Chorus;
using Timing.Delta5;
using Timing.ImmersionRC;
using Timing.RotorHazard;
using Tools;

namespace Timing
{
    public enum TimingSystemType
    {
        Dummy,
        Test,
        LapRF = 2,
        LapRF8Way = 2,
        Video,
        Delta5,
        RotorHazard,
        Chorus,
        Manual,
        Other,
    }

    public class ListeningFrequency
    {
        public int Frequency { get; set; }
        public float SensitivityFactor { get; set; }
        public ListeningFrequency(int freq, float sensitivity)
        {
            Frequency = freq;
            SensitivityFactor = sensitivity;
        }

        public override string ToString()
        {
            return Frequency + "mhz(" + (SensitivityFactor * 100) + "%)";
        }
    }

    public enum TimingSystemRole
    {
        Primary,
        Split
    }

    public interface ITimingSystem : IDisposable
    {
        TimingSystemType Type { get; }

        bool Connected { get; }

        /// <summary>Tries to connect to the timing system.</summary>  
        /// <returns> true on success.</returns>
        bool Connect();

        /// <summary> Gracefully disconnects from the timing system.</summary>  
        /// <returns>Return true if it disconnected gracefully.</returns>
        bool Disconnect();

        /// <summary>  
        /// Sets the listening frequencies on the timing system. Frequencies will be given in mhz. Eg 5880 for Raceband 7. 
        /// This will be called at prior to the start of every race.
        ///</summary>  
        /// <returns> Returning true if it set ok. 
        /// Returning false will cancel race start and system will attempt to Connect();
        /// </returns> 
        bool SetListeningFrequencies(IEnumerable<ListeningFrequency> newFrequencies);

        /// <summary>  
        /// Start the system listening for detection events. </summary>  
        /// <returns> 
        /// Return true if it started ok. 
        /// Returning false will cancel race start and system will attempt to Connect();
        /// </returns>  
        bool StartDetection();

        /// <summary>  
        /// Stops the system listening for detection events.  </summary>  
        /// <returns> 
        /// Return true if it stopped ok.
        /// </returns>  
        bool EndDetection();

        int MaxPilots { get; }

        /// <summary>  
        /// Call this event when a lap has been detected. 
        /// void DetectionEventDelegate(int frequency, DateTime time)
        /// First parameter is frequency in mhz (ie 5880) and the second parameter is the absolute time of the event. 
        /// </summary>  
        event DetectionEventDelegate OnDetectionEvent;

        TimingSystemSettings Settings { get; set; }

        IEnumerable<StatusItem> Status { get; }
    }

    public struct StatusItem
    {
        public string Value { get; set; }
        public bool StatusOK { get; set; }
    }

    [XmlInclude(typeof(DummySettings))]
    [XmlInclude(typeof(LapRFSettings))]
    [XmlInclude(typeof(LapRFSettingsUSB))]
    [XmlInclude(typeof(LapRFSettingsEthernet))]
    [XmlInclude(typeof(VideoTimingSettings))]
    [XmlInclude(typeof(Delta5TimingSettings))]
    [XmlInclude(typeof(ChorusSettings))]
    [XmlInclude(typeof(RotorHazardSettings))]
    public class TimingSystemSettings
    {
        [Category("System Settings")]
        public TimingSystemRole Role { get; set; }

        [Category("Speed Calculation (0 to disable)")]
        [DisplayName("Sector Length (Meters)")]
        public float SectorLengthMeters { get; set; }

        private static string timingSystemFilename = @"data/TimingSystemSettings.xml";

        public override string ToString()
        {
            return GetType().Name;
        }

        public static TimingSystemSettings[] Read()
        {
            try
            {
                TimingSystemSettings[] s = Tools.IOTools.Read<TimingSystemSettings>(timingSystemFilename);
                if (s == null || s.Length == 0)
                {
                    s = new TimingSystemSettings[] { };
                }

                Write(s);

                return s;
            }
            catch
            {
                return new TimingSystemSettings[] { };
            }
        }

        public static void Write(TimingSystemSettings[] settings)
        {
            Tools.IOTools.Write(timingSystemFilename, settings);
        }
    }

    /// <summary> 
    /// The main delegate for an actual detection event. 
    /// Frequency is mhz, ie 5880
    /// Time is the absolute time of the event. 
    /// Peak is the signal peak
    /// Sector is the sector of the track. 
    /// </summary>  
    public delegate void DetectionEventDelegate(ITimingSystem system, int frequency, DateTime time, int peak);

    public interface ITimingSystemWithRSSI : ITimingSystem
    {
        IEnumerable<RSSI> GetRSSI();
    }
    public struct RSSI
    {
        public ITimingSystem TimingSystem { get; set; }
        public int Frequency { get; set; }
        
        public float CurrentRSSI { get; set; }
        public float ScaleMin { get; set; }
        public float ScaleMax { get; set; }

        public bool Detected { get; set; }

    }
}


