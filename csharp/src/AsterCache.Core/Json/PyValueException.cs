namespace AsterCache.Core.Json;

/// Python ValueError (json.JSONDecodeError 포함) 에 해당합니다. 계획·추적 입력이 잘못됐을 때 던집니다.
public sealed class PyValueException : Exception
{
    public PyValueException(string message) : base(message)
    {
    }
}
