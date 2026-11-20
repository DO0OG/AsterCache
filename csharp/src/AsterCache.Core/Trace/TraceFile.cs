using System.Text;
using System.Text.Json;
using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// 트레이스 파일을 읽은 결과. Rows 가 null 이면 행 목록이 아닌 파일입니다 ("bad trace file").
/// RowError 는 Rows 의 마지막 행 다음에 있던 첫 잘못된 행의 오류입니다: 옛 방식에서 행을 앞에서부터 <see cref="TraceEvents.FromJson"/> 으로 바꾸다 던지던 그 예외로, 앞의 행을 처리한 뒤에 던져야 합니다.
public sealed record TraceFileRows(List<TraceEvent>? Rows, PyValueException? RowError);

/// 트레이스 파일 ([{path, offset, length, cost, ...}, ...] 이나 {"ranges": [...]}) 을 문서 트리 없이 파일에서 바로 행으로 읽습니다.
/// 읽은 행의 값·순서·오류는 <c>JsonRead.ReadFile</c> + <see cref="TraceEvents.FromJson"/> 과 같고 (대조 시험이 있음),
/// 같은 경로 문자열은 하나를 나눠 씁니다.
public static class TraceFile
{
    private enum Kind { Absent, Null, Bool, Long, Double, String, Other }

    private struct Slot
    {
        public Kind Kind;
        public long Long; // Bool 은 0/1
        public double Double;
        public string? String;
    }

    public static TraceFileRows Read(string path)
    {
        var s = new JsonFileReader(path);
        try
        {
            List<TraceEvent>? rows = null;
            PyValueException? rowError = null;
            JsonException? syntaxError = null;
            var paths = new PathPool();
            try
            {
                s.ReadNext();
                switch (s.Reader.TokenType)
                {
                    case JsonTokenType.StartArray:
                        (rows, rowError) = ReadRows(ref s, paths);
                        break;
                    case JsonTokenType.StartObject:
                        while (s.ReadNext() && s.Reader.TokenType == JsonTokenType.PropertyName)
                        {
                            bool ranges = s.TextEquals("ranges"u8);
                            s.ReadNext();
                            if (ranges && s.Reader.TokenType == JsonTokenType.StartArray)
                            {
                                (rows, rowError) = ReadRows(ref s, paths);
                                continue;
                            }

                            if (ranges)
                            {
                                (rows, rowError) = (null, null); // 같은 키가 또 나오면 마지막 값이 이김
                            }

                            JsonRead.ReadValue(ref s);
                        }

                        break;
                    default:
                        JsonRead.ReadValue(ref s);
                        break;
                }

                s.ReadNext(); // 끝 뒤에 다른 내용이 없는지 확인
            }
            catch (JsonException e)
            {
                syntaxError = e;
            }

            s.Finish(syntaxError);
            return new TraceFileRows(rows, rowError);
        }
        finally
        {
            s.Dispose();
        }
    }

    /// 지금 토큰(배열 시작)의 행들을 읽습니다. 잘못된 행을 만나면 그 오류를 적고, 나머지는 구문·값 변환 검사만 하며 읽습니다.
    private static (List<TraceEvent> Rows, PyValueException? Error) ReadRows(ref JsonFileReader s, PathPool paths)
    {
        var rows = new List<TraceEvent>();
        PyValueException? error = null;
        while (s.ReadNext() && s.Reader.TokenType != JsonTokenType.EndArray)
        {
            if (error is not null)
            {
                JsonRead.ReadValue(ref s);
                continue;
            }

            try
            {
                rows.Add(ReadRow(ref s, paths));
            }
            catch (PyValueException e)
            {
                error = e;
            }
        }

        return (rows, error);
    }

    /// <see cref="TraceEvents.FromJson"/> 과 같은 검사와 같은 순서·같은 문구. 행 전체를 읽은 뒤에 던집니다 (뒤의 구문 오류가 더 우선이므로).
    private static TraceEvent ReadRow(ref JsonFileReader s, PathPool paths)
    {
        if (s.Reader.TokenType != JsonTokenType.StartObject)
        {
            JsonRead.ReadValue(ref s);
            throw new PyValueException("bad row");
        }

        Slot path = default, offset = default, length = default, cost = default, tMs = default, observed = default, group = default, overflow = default, ignored = default;
        while (s.ReadNext() && s.Reader.TokenType == JsonTokenType.PropertyName)
        {
            ref Slot slot = ref ignored;
            bool isPath = false;
            if (s.TextEquals("path"u8))
            {
                slot = ref path;
                isPath = true;
            }
            else if (s.TextEquals("offset"u8))
            {
                slot = ref offset;
            }
            else if (s.TextEquals("length"u8))
            {
                slot = ref length;
            }
            else if (s.TextEquals("cost"u8))
            {
                slot = ref cost;
            }
            else if (s.TextEquals("t_ms"u8))
            {
                slot = ref tMs;
            }
            else if (s.TextEquals("observed"u8))
            {
                slot = ref observed;
            }
            else if (s.TextEquals("range_group"u8))
            {
                slot = ref group;
            }
            else if (s.TextEquals("overflow"u8))
            {
                slot = ref overflow;
            }

            s.ReadNext();
            slot = ReadSlot(ref s, isPath ? paths : null); // 같은 키가 또 나오면 마지막 값이 이김
        }

        if (path.Kind != Kind.String || offset.Kind != Kind.Long || length.Kind != Kind.Long)
        {
            throw new PyValueException("bad row");
        }

        double costValue = TraceEvents.DefaultCost;
        switch (cost.Kind)
        {
            case Kind.Absent:
                break;
            case Kind.Long:
                costValue = cost.Long;
                break;
            case Kind.Double:
                costValue = cost.Double;
                break;
            default:
                throw new PyValueException("bad cost");
        }

        long? tMsValue = tMs.Kind switch
        {
            Kind.Absent or Kind.Null => null,
            Kind.Long when tMs.Long >= 0 => tMs.Long,
            _ => throw new PyValueException("bad t_ms"),
        };
        bool observedValue = observed.Kind switch
        {
            Kind.Absent => true,
            Kind.Bool => observed.Long != 0,
            _ => throw new PyValueException("bad observed"),
        };
        long? groupValue = group.Kind switch
        {
            Kind.Absent => null,
            Kind.Long when group.Long >= 0 => group.Long,
            _ => throw new PyValueException("bad range_group"),
        };
        bool overflowValue = overflow.Kind switch
        {
            Kind.Absent => false,
            Kind.Bool => overflow.Long != 0,
            _ => throw new PyValueException("bad overflow"),
        };

        return new TraceEvent(path.String!, offset.Long, length.Long, costValue, tMsValue, observedValue, groupValue, overflowValue);
    }

    /// 지금 토큰의 값 하나. paths 가 있으면 문자열을 경로 풀에서 나눠 씁니다.
    private static Slot ReadSlot(ref JsonFileReader s, PathPool? paths)
    {
        switch (s.Reader.TokenType)
        {
            case JsonTokenType.String:
                return new Slot { Kind = Kind.String, String = paths is null ? s.GetString() : paths.Get(ref s) };
            case JsonTokenType.Number:
                if (!s.ReadNumber(out bool isDouble, out long integer, out double real))
                {
                    return new Slot { Kind = Kind.Other }; // 변환 오류는 읽기 틀이 적어 두었고, 이 값은 쓰이지 않음
                }

                return isDouble ? new Slot { Kind = Kind.Double, Double = real } : new Slot { Kind = Kind.Long, Long = integer };
            case JsonTokenType.True:
                return new Slot { Kind = Kind.Bool, Long = 1 };
            case JsonTokenType.False:
                return new Slot { Kind = Kind.Bool };
            case JsonTokenType.Null:
                return new Slot { Kind = Kind.Null };
            default:
                JsonRead.ReadValue(ref s); // 객체·배열은 값으로 쓰이지 않지만 구문·값 변환은 검사
                return new Slot { Kind = Kind.Other };
        }
    }

    /// 파일 하나 안에서 같은 경로 문자열을 하나로 합칩니다 (고유 경로 수천 개 대 행 수십만 개).
    /// 바로 앞 행과 같은 경로면 문자열을 만들지 않고 바이트만 비교합니다.
    private sealed class PathPool
    {
        private readonly Dictionary<string, string> _all = new(StringComparer.Ordinal);
        private string? _last;
        private byte[] _lastUtf8 = [];

        public string Get(ref JsonFileReader s)
        {
            if (_last is not null && s.TextEquals(_lastUtf8))
            {
                return _last;
            }

            string text = s.GetString();
            if (!_all.TryGetValue(text, out string? shared))
            {
                _all[text] = shared = text;
            }

            _last = shared;
            _lastUtf8 = Encoding.UTF8.GetBytes(shared);
            return shared;
        }
    }
}
