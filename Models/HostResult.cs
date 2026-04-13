using System;

namespace SunshineLibrary.Models
{
    public enum HostResultKind
    {
        Ok,
        AuthFailed,
        CertMismatch,
        CertMissing,
        Unreachable,
        Timeout,
        ServerError,
        Cancelled
    }

    public class HostResult
    {
        public HostResultKind Kind { get; }
        public string Message { get; }
        public int? StatusCode { get; }
        public string NewCertFingerprintSpkiSha256 { get; }

        protected HostResult(HostResultKind kind, string message, int? statusCode = null, string newFingerprint = null)
        {
            Kind = kind;
            Message = message;
            StatusCode = statusCode;
            NewCertFingerprintSpkiSha256 = newFingerprint;
        }

        public bool IsOk => Kind == HostResultKind.Ok;

        public static HostResult Ok() => new HostResult(HostResultKind.Ok, null);
        public static HostResult AuthFailed() => new HostResult(HostResultKind.AuthFailed, null);
        public static HostResult CertMismatch(string newFingerprint) => new HostResult(HostResultKind.CertMismatch, null, null, newFingerprint);
        public static HostResult CertMissing() => new HostResult(HostResultKind.CertMissing, null);
        public static HostResult Unreachable(string message) => new HostResult(HostResultKind.Unreachable, message);
        public static HostResult Timeout() => new HostResult(HostResultKind.Timeout, null);
        public static HostResult ServerError(int code, string message) => new HostResult(HostResultKind.ServerError, message, code);
        public static HostResult Cancelled() => new HostResult(HostResultKind.Cancelled, null);
    }

    public class HostResult<T> : HostResult
    {
        public T Value { get; }

        private HostResult(T value) : base(HostResultKind.Ok, null) { Value = value; }
        private HostResult(HostResultKind kind, string message, int? statusCode, string newFingerprint)
            : base(kind, message, statusCode, newFingerprint) { }

        public static HostResult<T> Ok(T value) => new HostResult<T>(value);
        public new static HostResult<T> AuthFailed() => new HostResult<T>(HostResultKind.AuthFailed, null, null, null);
        public new static HostResult<T> CertMismatch(string newFingerprint) => new HostResult<T>(HostResultKind.CertMismatch, null, null, newFingerprint);
        public new static HostResult<T> CertMissing() => new HostResult<T>(HostResultKind.CertMissing, null, null, null);
        public new static HostResult<T> Unreachable(string message) => new HostResult<T>(HostResultKind.Unreachable, message, null, null);
        public new static HostResult<T> Timeout() => new HostResult<T>(HostResultKind.Timeout, null, null, null);
        public new static HostResult<T> ServerError(int code, string message) => new HostResult<T>(HostResultKind.ServerError, message, code, null);
        public new static HostResult<T> Cancelled() => new HostResult<T>(HostResultKind.Cancelled, null, null, null);

        public HostResult AsStatus() => new _Status(Kind, Message, StatusCode, NewCertFingerprintSpkiSha256);

        private class _Status : HostResult
        {
            public _Status(HostResultKind k, string m, int? sc, string nfp) : base(k, m, sc, nfp) { }
        }
    }
}
