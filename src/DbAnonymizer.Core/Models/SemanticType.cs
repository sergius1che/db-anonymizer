// Models/SemanticType.cs
namespace DbAnonymizer.Core.Models;

public enum SemanticType
{
    Unknown = 0,
    FirstName,
    LastName,
    MiddleName,
    FullName,
    Email,
    Login,
    Phone,
    CardNumber,
    Inn,
    Snils,
    PassportNumber,
    Address,
    CompanyName,
    BirthDate,
    PasswordHash,
    Salt,
    Token,
    ExternalId,
    FreeText,
    Json
}
