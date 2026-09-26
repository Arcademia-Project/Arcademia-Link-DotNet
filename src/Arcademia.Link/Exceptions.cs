using System;

namespace Arcademia.Link
{
    public class ArcademiaLinkException : Exception
    {
        public string Error { get; }
        public int StatusCode { get; }

        public ArcademiaLinkException(string error, string message, int statusCode = 0, Exception inner = null)
            : base(message, inner)
        {
            Error = error;
            StatusCode = statusCode;
        }
    }

    public sealed class ArcademiaSignInRequiredException : ArcademiaLinkException
    {
        public ArcademiaSignInRequiredException(string message)
            : base("sign_in_required", message, 401) { }
    }

    public sealed class ArcademiaSignInCancelledException : ArcademiaLinkException
    {
        public ArcademiaSignInCancelledException(string error, string message)
            : base(error, message) { }
    }
}
