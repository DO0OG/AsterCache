using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace AsterCache.Core.Json;

/// 파일을 64 KiB 조각으로 읽어 Utf8JsonReader 에 먹입니다. 파일 전체를 바이트 배열·문자열·문서 트리로 올리지 않습니다.
/// 오류의 우선순위는 옛 방식(전체 UTF-8 해석 → JsonDocument 구문 검사 → 값 변환)과 같습니다:
/// 잘못된 UTF-8 이 어디에 있든 가장 먼저, 그다음 구문 오류, 마지막이 값 변환 오류(<see cref="ConvertError"/>, 처음 것만)입니다.
/// 그래서 오류를 만나도 파일 끝까지 UTF-8 만은 검사한 뒤 <see cref="Finish"/> 가 가장 높은 것을 던집니다.
internal ref struct JsonFileReader
{
    private const int ChunkBytes = 64 * 1024;

    private readonly FileStream _file;
    private byte[] _buf;
    private int _end;
    private int _validated;
    private int _spanStart;
    private long _remaining;
    private bool _final;
    private bool _badUtf8;

    public Utf8JsonReader Reader;

    /// 값 변환 단계의 첫 오류 (범위 밖 정수, 변환할 수 없는 문자열·실수). 구문 오류가 없을 때만 던져집니다.
    public PyValueException? ConvertError;

    public JsonFileReader(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        _buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        _remaining = _file.Length;
        Fill();
        int skip = _end >= 3 && _buf[0] == 0xEF && _buf[1] == 0xBB && _buf[2] == 0xBF ? 3 : 0; // utf-8-sig
        _spanStart = skip;
        Reader = new Utf8JsonReader(_buf.AsSpan(skip, _end - skip), _final, new JsonReaderState(new JsonReaderOptions { MaxDepth = 512 }));
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buf);
        _file.Dispose();
    }

    /// 다음 토큰으로 갑니다. 문서가 끝났으면 false (끝 뒤에 다른 내용이 있으면 reader 가 JsonException).
    public bool ReadNext()
    {
        while (true)
        {
            if (Reader.Read())
            {
                return true;
            }

            if (_final)
            {
                return false;
            }

            Refill();
        }
    }

    /// 지금 토큰의 문자열. 문자로 바꿀 수 없으면(예: 짝 없는 서로게이트) 값 변환 오류로 적고 빈 문자열을 줍니다.
    public string GetString()
    {
        try
        {
            return Reader.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException e)
        {
            ConvertError ??= new PyValueException("invalid JSON: " + e.Message);
            return string.Empty;
        }
    }

    /// 지금 문자열 토큰이 text(UTF-8) 와 같은지. 문자로 바꿀 수 없는 문자열이면 <see cref="GetString"/> 처럼 값 변환 오류로 적고 false.
    public bool TextEquals(ReadOnlySpan<byte> text)
    {
        try
        {
            return Reader.ValueTextEquals(text);
        }
        catch (InvalidOperationException e)
        {
            ConvertError ??= new PyValueException("invalid JSON: " + e.Message);
            return false;
        }
    }

    /// 지금 토큰의 숫자. Python 의 type(x) is int 처럼 엄격합니다: 소수점·지수가 있으면 실수, 아니면 int64 (범위 밖은 변환 오류).
    /// 변환 오류면 <see cref="ConvertError"/> 에 적고 false.
    public bool ReadNumber(out bool isDouble, out long integer, out double real)
    {
        integer = 0;
        real = 0;
        ReadOnlySpan<byte> raw = Reader.ValueSpan;
        isDouble = raw.IndexOfAny((byte)'.', (byte)'e', (byte)'E') >= 0;
        if (isDouble)
        {
            if (Reader.TryGetDouble(out real))
            {
                return true;
            }

            ConvertError ??= new PyValueException("invalid JSON: unreadable number " + Encoding.UTF8.GetString(raw));
            return false;
        }

        if (Reader.TryGetInt64(out integer))
        {
            return true;
        }

        ConvertError ??= new PyValueException("integer out of int64 range: " + Encoding.UTF8.GetString(raw));
        return false;
    }

    /// 읽기를 마칩니다. 파일 끝까지 UTF-8 을 검사한 뒤, 가장 높은 우선순위의 오류를 던집니다 (없으면 그냥 돌아옴).
    public void Finish(JsonException? syntaxError)
    {
        while (!_final)
        {
            int tail = _end - _validated;
            Buffer.BlockCopy(_buf, _validated, _buf, 0, tail);
            _end = tail;
            _validated = 0;
            Fill();
        }

        if (_badUtf8)
        {
            throw new PyValueException("invalid UTF-8: invalid byte sequence");
        }

        if (syntaxError is not null)
        {
            throw new PyValueException("invalid JSON: " + syntaxError.Message);
        }

        if (ConvertError is not null)
        {
            throw ConvertError;
        }
    }

    private void Refill()
    {
        JsonReaderState state = Reader.CurrentState;
        int consumed = _spanStart + (int)Reader.BytesConsumed;
        if (consumed == 0)
        {
            // 토큰 하나(긴 문자열)가 버퍼보다 큼
            byte[] bigger = ArrayPool<byte>.Shared.Rent(_buf.Length * 2);
            Buffer.BlockCopy(_buf, 0, bigger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = bigger;
        }
        else
        {
            Buffer.BlockCopy(_buf, consumed, _buf, 0, _end - consumed);
            _end -= consumed;
            _validated = Math.Max(0, _validated - consumed);
        }

        Fill();
        _spanStart = 0;
        Reader = new Utf8JsonReader(_buf.AsSpan(0, _end), _final, state);
    }

    /// 버퍼 빈 곳을 파일에서 채우고, 새로 들어온 바이트의 UTF-8 을 검사합니다.
    private void Fill()
    {
        int want = (int)Math.Min(_buf.Length - _end, _remaining); // 연 시점의 길이까지만 (옛 방식과 같음)
        _file.ReadExactly(_buf.AsSpan(_end, want));
        _end += want;
        _remaining -= want;
        _final = _remaining == 0;

        int limit = _end;
        if (!_final)
        {
            // 조각 끝에서 잘린 여러 바이트 문자는 다음 조각과 함께 검사
            for (int back = 1; back <= 3 && limit - back >= _validated; back++)
            {
                byte b = _buf[limit - back];
                if ((b & 0xC0) == 0x80)
                {
                    continue;
                }

                if ((b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1) > back)
                {
                    limit -= back;
                }

                break;
            }
        }

        if (!_badUtf8 && !Utf8.IsValid(_buf.AsSpan(_validated, limit - _validated)))
        {
            _badUtf8 = true;
        }

        _validated = limit;
    }
}
