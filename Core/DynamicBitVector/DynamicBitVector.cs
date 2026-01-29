using Core.GOAP;
using System;
using System.Collections;
using System.Collections.Generic;

public sealed class DynamicBitVector
{
    private readonly BitArray _bits;
    private int _nextBit;

    public DynamicBitVector(int initialSize = 64)
    {
        _bits = new BitArray(initialSize);
        _nextBit = 0;
    }

    public DynamicBitVector(DynamicBitVector other)
    {
        if (other == null)
            throw new ArgumentNullException(nameof(other));

        _bits = new BitArray(other._bits); // deep copy
        _nextBit = other._nextBit;
    }

    public int Length
    {
        get => _bits.Length;
        set => _bits.Length = value;
    }

    public void ClearAll(bool value = false) => _bits.SetAll(value);

    public void SetFlag(int bitIndex, bool value)
    {
        if (bitIndex < 0) throw new ArgumentOutOfRangeException(nameof(bitIndex));
        EnsureCapacity(bitIndex + 1);
        _bits[bitIndex] = value;
        if (bitIndex >= _nextBit) _nextBit = bitIndex + 1; // keep "used bits" consistent
    }

    public bool GetFlag(int bitIndex)
    {
        if (bitIndex < 0) throw new ArgumentOutOfRangeException(nameof(bitIndex));
        return bitIndex < _bits.Length && _bits[bitIndex];
    }

    public bool this[GoapKey key]
    {
        get => GetFlag((int)key);
        set => SetFlag((int)key, value);
    }

    // ---------- FLAGS ----------

    public int CreateFlag()
    {
        EnsureCapacity(_nextBit + 1);
        return _nextBit++;
    }

    public bool this[int flag]
    {
        get => _bits[flag];
        set => _bits[flag] = value;
    }

    // ---------- SECTIONS ----------

    public Section CreateSection(int bitCount)
    {
        if (bitCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitCount));

        EnsureCapacity(_nextBit + bitCount);

        var section = new Section(_nextBit, bitCount);
        _nextBit += bitCount;
        return section;
    }

    public int GetSection(Section section)
    {
        int value = 0;
        for (int i = 0; i < section.BitCount; i++)
        {
            if (_bits[section.Offset + i])
                value |= 1 << i;
        }
        return value;
    }

    public void SetSection(Section section, int value)
    {
        if (value < 0 || value >= (1 << section.BitCount))
            throw new ArgumentOutOfRangeException(nameof(value));

        for (int i = 0; i < section.BitCount; i++)
        {
            _bits[section.Offset + i] = ((value >> i) & 1) == 1;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_bits.Length < required)
            _bits.Length = required;
    }

    // ---------- DEBUG / UTILITY ----------

    public override string ToString()
    {
        char[] chars = new char[_bits.Length];
        for (int i = 0; i < _bits.Length; i++)
            chars[_bits.Length - i - 1] = _bits[i] ? '1' : '0';
        return new string(chars);
    }
}

public readonly struct Section
{
    public readonly int Offset;
    public readonly int BitCount;

    public Section(int offset, int bitCount)
    {
        Offset = offset;
        BitCount = bitCount;
    }
}
