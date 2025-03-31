using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WebApplication2;

public static class DbHelpers
{
    public static SqliteConnection Connection
    {
        get
        {
            var connection = new SqliteConnection("identifier.sqlite");
            connection.Open();
            return connection;
        }
    }
    
    public static string HashPassword(string password)
    {
        // Преобразуем пароль в массив байтов
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));

        // Преобразуем массив байтов в строку
        var builder = new StringBuilder();
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2")); // Форматируем байты в шестнадцатеричную строку
        }
        return builder.ToString();
    }


}