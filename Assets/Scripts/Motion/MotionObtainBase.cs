using System;
using System.Collections.Generic;
using UnityEngine;

public abstract class MotionObtainBase : MotionObtain {
    [SerializeField] private int historyCapacityPerType = 256;

    private readonly Dictionary<Type, object> _buffers = new Dictionary<Type, object>();

    private sealed class RingBuffer<T> where T : struct, ITimeSeriesData {
        private readonly T[] _buffer;
        private int _head;
        private int _count;

        public RingBuffer(int capacity){
            if (capacity <= 0) capacity = 1;
            _buffer = new T[capacity];
            _head = 0;
            _count = 0;
        }

        public int Count => _count;
        public int Capacity => _buffer.Length;

        public void Clear(){ _head = 0; _count = 0; }

        public void Add(in T value){
            _buffer[_head] = value;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }

        public bool TryGetLatest(out T value){
            if (_count == 0){ value = default; return false; }
            int idx = (_head - 1 + _buffer.Length) % _buffer.Length;
            value = _buffer[idx];
            return true;
        }

        public int CopyHistory(DateTime from, DateTime to, Span<T> dst){
            if (_count == 0 || to < from || dst.Length == 0) return 0;

            int cap = _buffer.Length;
            int oldest = (_head - _count + cap) % cap;

            // Helpers to access logical sequence [0.._count)
            DateTime TimeAt(int logical){
                int idx = (oldest + logical) % cap;
                return _buffer[idx].Timestamp;
            }

            // lower_bound for 'from'
            int lo = 0, hi = _count;
            while (lo < hi){
                int mid = lo + ((hi - lo) >> 1);
                if (TimeAt(mid) >= from) hi = mid; else lo = mid + 1;
            }
            int start = lo; // first >= from

            // upper_bound for 'to' (first > to)
            lo = start; hi = _count;
            while (lo < hi){
                int mid = lo + ((hi - lo) >> 1);
                if (TimeAt(mid) > to) hi = mid; else lo = mid + 1;
            }
            int endExclusive = lo; // one past last <= to

            int needed = endExclusive - start;
            if (needed <= 0) return 0;
            int toWrite = needed < dst.Length ? needed : dst.Length;

            int physStart = (oldest + start) % cap;
            int firstLen = Math.Min(toWrite, cap - physStart);
            for (int i = 0; i < firstLen; i++) dst[i] = _buffer[physStart + i];
            int rem = toWrite - firstLen;
            for (int i = 0; i < rem; i++) dst[firstLen + i] = _buffer[i];

            return toWrite;
        }

        public int CopyLastN(int n, Span<T> dst){
            if (_count == 0 || n <= 0 || dst.Length == 0) return 0;
            int toCopy = Math.Min(n, Math.Min(_count, dst.Length));
            int cap = _buffer.Length;
            int endExclusive = _head; // logical end (one past last)
            int start = (endExclusive - toCopy + cap) % cap;
            int firstLen = Math.Min(toCopy, cap - start);
            for (int i = 0; i < firstLen; i++) dst[i] = _buffer[start + i];
            int rem = toCopy - firstLen;
            for (int i = 0; i < rem; i++) dst[firstLen + i] = _buffer[i];
            return toCopy;
        }
    }

    private RingBuffer<T> GetBuffer<T>() where T : struct, ITimeSeriesData {
        var type = typeof(T);
        if (_buffers.TryGetValue(type, out var obj)) return (RingBuffer<T>)obj;
        var buf = new RingBuffer<T>(historyCapacityPerType);
        _buffers[type] = buf;
        return buf;
    }

    protected void ClearAllHistory(){ _buffers.Clear(); }
    protected void ClearHistory<T>() where T : struct, ITimeSeriesData { GetBuffer<T>().Clear(); }

    protected void Record<T>(in T sample) where T : struct, ITimeSeriesData {
        GetBuffer<T>().Add(sample);
    }

    public override bool TryGetLatestData<T>(out T data) {
        return GetBuffer<T>().TryGetLatest(out data);
    }

    public override int CopyHistory<T>(DateTime from, DateTime to, Span<T> dst) {
        return GetBuffer<T>().CopyHistory(from, to, dst);
    }

    public override int CopyLastN<T>(int n, Span<T> dst) {
        return GetBuffer<T>().CopyLastN(n, dst);
    }
}