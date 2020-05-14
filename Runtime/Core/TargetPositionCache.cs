using UnityEngine;
using System.Collections.Generic;
using System;

namespace Cinemachine
{
    internal class TargetPositionCache
    {
        public static bool UseCache { get; set; }
        public enum Mode { Disabled, Record, Playback }
        
        static Mode m_CacheMode = Mode.Disabled;

        public static Mode CacheMode
        {
            get => m_CacheMode;
            set
            {
                if (value == m_CacheMode)
                    return;
                m_CacheMode = value;
                switch (m_CacheMode)
                {
                    default: case Mode.Disabled: ClearCache(); break;
                    case Mode.Record: InitCache(); break;
                    case Mode.Playback: CreatePlaybackCurves(); break;
                }
            }
        }

        public static float CurrentTime { get; set; }

        class CacheEntry
        {
            public AnimationCurve X = new AnimationCurve();
            public AnimationCurve Y = new AnimationCurve();
            public AnimationCurve Z = new AnimationCurve();
            public AnimationCurve RotX = new AnimationCurve();
            public AnimationCurve RotY = new AnimationCurve();
            public AnimationCurve RotZ = new AnimationCurve();

            struct RecordingItem : IComparable<RecordingItem>
            {
                public float Time;
                public Vector3 Pos;
                public Vector3 Rot;
                public int CompareTo(RecordingItem other) { return Time.CompareTo(other.Time); }
            }
            List<RecordingItem> RawItems = new List<RecordingItem>();

            const float kResolution = 0.033f;

            public void AddRawItem(float time, Transform target)
            {
                var n = RawItems.Count;
                if (n == 0 || Mathf.Abs(RawItems[n-1].Time - time) >= kResolution)
                    RawItems.Add(new RecordingItem
                    {
                        Time = time,
                        Pos = target.position,
                        Rot = target.rotation.eulerAngles
                    });
            }

            public void CreateCurves()
            {
                var XList = new List<Keyframe>();
                var YList = new List<Keyframe>();
                var ZList = new List<Keyframe>();
                var RotXList = new List<Keyframe>();
                var RotYList = new List<Keyframe>();
                var RotZList = new List<Keyframe>();

                RawItems.Sort();
                float time = float.MaxValue;
                for (int i = 0; i < RawItems.Count; ++i)
                {
                    var item = RawItems[i];
                    if (Mathf.Abs(item.Time - time) < kResolution)
                        continue;
                    time = item.Time;

                    SmoothAddKey(XList, time, item.Pos.x);
                    SmoothAddKey(YList, time, item.Pos.y);
                    SmoothAddKey(ZList, time, item.Pos.z);
                    SmoothAddKey(RotXList, time, item.Rot.x);
                    SmoothAddKey(RotYList, time, item.Rot.y);
                    SmoothAddKey(RotZList, time, item.Rot.z);
                }
                RawItems.Clear();

                X = new AnimationCurve(XList.ToArray());
                Y = new AnimationCurve(YList.ToArray());
                Z = new AnimationCurve(ZList.ToArray());
                RotX = new AnimationCurve(RotXList.ToArray());
                RotY = new AnimationCurve(RotYList.ToArray());
                RotZ = new AnimationCurve(RotZList.ToArray());
            }

            void SmoothAddKey(List<Keyframe> keys, float time, float value)
            {
                var n = keys.Count;
                if (n == 0)
                    keys.Add(new Keyframe(time, value));
                else
                {
                    var k = keys[keys.Count - 1];
                    var t = (value - k.value) / (time - k.time);
                    keys.Add(new Keyframe(time, value, t, t));
                }
            }
        }

        static Dictionary<Transform, CacheEntry> m_Cache;

        public struct TimeRange
        {
            public float Start;
            public float End;

            public bool IsEmpty => End < Start;
            public bool Contains(float time) => time >= Start && time <= End;
            public static TimeRange Empty 
                { get => new TimeRange { Start = float.MaxValue, End = float.MinValue }; }

            public void Include(float time)
            {
                Start = Mathf.Min(Start, time);
                End = Mathf.Max(End, time);
            }
        }
        static TimeRange m_CacheTimeRange;
        public static TimeRange CacheTimeRange { get => m_CacheTimeRange; }
        public static bool HasHurrentTime { get => m_CacheTimeRange.Contains(CurrentTime); }

        static void ClearCache()
        {
            m_Cache = null;
            m_CacheTimeRange = TimeRange.Empty;
        }

        static void InitCache()
        {
            m_Cache = new Dictionary<Transform, CacheEntry>();
            m_CacheTimeRange = TimeRange.Empty;
        }

        static void CreatePlaybackCurves()
        {
            if (m_Cache == null)
                m_Cache = new Dictionary<Transform, CacheEntry>();
            var iter = m_Cache.GetEnumerator();
            while (iter.MoveNext())
                iter.Current.Value.CreateCurves();
        }

        const float kWraparoundSlush = 0.1f;

        /// <summary>
        /// If Recording, will log the target position at the CurrentTime.
        /// Otherwise, will fetch the cached position at CurrentTime.
        /// </summary>
        /// <param name="target">Target whose transform is tracked</param>
        /// <param name="position">Target's position at CurrentTime</param>
        public static Vector3 GetTargetPosition(Transform target)
        {
            if (!UseCache || CacheMode == Mode.Disabled)
                return target.position;

            // Wrap around during record?
            if (CacheMode == Mode.Record 
                && !m_CacheTimeRange.IsEmpty 
                && CurrentTime < m_CacheTimeRange.Start - kWraparoundSlush)
            {
                ClearCache();
                InitCache();
            }

            if (CacheMode == Mode.Playback && !HasHurrentTime)
                return target.position;

            if (!m_Cache.TryGetValue(target, out var entry))
            {
                if (CacheMode != Mode.Record)
                    return target.position;

                entry = new CacheEntry();
                m_Cache.Add(target, entry);
            }
            if (CacheMode == Mode.Record)
            {
                entry.AddRawItem(CurrentTime, target);
                m_CacheTimeRange.Include(CurrentTime);
                return target.position;
            }
            return new Vector3(
                entry.X.Evaluate(CurrentTime),
                entry.Y.Evaluate(CurrentTime),
                entry.Z.Evaluate(CurrentTime));
        }

        /// <summary>
        /// If Recording, will log the target rotation at the CurrentTime.
        /// Otherwise, will fetch the cached position at CurrentTime.
        /// </summary>
        /// <param name="target">Target whose transform is tracked</param>
        /// <param name="rotation">Target's rotation at CurrentTime</param>
        public static Quaternion GetTargetRotation(Transform target)
        {
            if (CacheMode == Mode.Disabled)
                return target.rotation;

            // Wrap around during record?
            if (CacheMode == Mode.Record 
                && !m_CacheTimeRange.IsEmpty 
                && CurrentTime < m_CacheTimeRange.Start - kWraparoundSlush)
            {
                ClearCache();
                InitCache();
            }

            if (CacheMode == Mode.Playback && !HasHurrentTime)
                return target.rotation;

            if (!m_Cache.TryGetValue(target, out var entry))
            {
                if (CacheMode != Mode.Record)
                    return target.rotation;

                entry = new CacheEntry();
                m_Cache.Add(target, entry);
            }
            if (CacheMode == Mode.Record)
            {
                entry.AddRawItem(CurrentTime, target);
                m_CacheTimeRange.Include(CurrentTime);
                return target.rotation;
            }
            return Quaternion.Euler(
                entry.RotX.Evaluate(CurrentTime),
                entry.RotY.Evaluate(CurrentTime),
                entry.RotZ.Evaluate(CurrentTime));
        }
    }
}