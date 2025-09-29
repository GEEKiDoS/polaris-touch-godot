using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

class RC4
{
    const int RC4_SBOX_SIZE = 256;

    private readonly byte[] _key;
    private readonly byte[] _sbox = new byte[RC4_SBOX_SIZE];
    private ulong _a = 0;
    private ulong _b = 0;

    public RC4(string key)
    {
        _key = Encoding.ASCII.GetBytes(key);
        Reset();
    }

    public void Reset()
    {
        for (var i = 0; i < RC4_SBOX_SIZE; i++)
            _sbox[i] = (byte)i;

        if (_key.Length == 0)
            return;

        ulong j = 0;
        for (var i = 0; i < _sbox.Length; i++)
        {
            // update
            j = (j + _sbox[i] + _key[i % _key.Length]) % RC4_SBOX_SIZE;

            // swap
            var tmp = _sbox[i];
            _sbox[i] = _sbox[j];
            _sbox[j] = tmp;
        }
    }

    public void Crypt(Span<byte> target)
    {
        // iterate all bytes
        for (var pos = 0; pos < target.Length; pos++)
        {
            // update
            _a = (_a + 1) % RC4_SBOX_SIZE;
            _b = (_b + _sbox[_a]) % RC4_SBOX_SIZE;

            // swap
            var tmp = _sbox[_a];
            _sbox[_a] = _sbox[_b];
            _sbox[_b] = tmp;

            // crypt
            target[pos] ^= _sbox[(_sbox[_a] + _sbox[_b]) % RC4_SBOX_SIZE];
        }
    }
}
