using System.Security.Cryptography;

namespace ProxyNetworker.Server.Security;

public sealed class InitialAdminPasswordGenerator
{
    private const int PasswordLength = 12;
    private const string UppercaseCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string LowercaseCharacters = "abcdefghijkmnopqrstuvwxyz";
    private const string DigitCharacters = "23456789";
    private const string SymbolCharacters = "!@#$%^&*-_=+";
    private const string AllCharacters = UppercaseCharacters + LowercaseCharacters + DigitCharacters + SymbolCharacters;

    public string Generate()
    {
        Span<char> password = stackalloc char[PasswordLength];
        password[0] = Pick(UppercaseCharacters);
        password[1] = Pick(LowercaseCharacters);
        password[2] = Pick(DigitCharacters);
        password[3] = Pick(SymbolCharacters);

        for (var index = 4; index < password.Length; index++)
        {
            password[index] = Pick(AllCharacters);
        }

        for (var index = password.Length - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (password[index], password[swapIndex]) = (password[swapIndex], password[index]);
        }

        return new string(password);
    }

    private static char Pick(string characters)
    {
        return characters[RandomNumberGenerator.GetInt32(characters.Length)];
    }
}
