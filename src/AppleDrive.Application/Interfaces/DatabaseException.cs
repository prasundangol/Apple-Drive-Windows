namespace AppleDrive.Application.Interfaces;

/// <summary>A failure of the local index database, independent of the database engine.</summary>
public sealed class DatabaseException(string message, Exception innerException) : Exception(message, innerException);
