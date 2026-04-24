using System;
using System.Text.Json;

class Program
{
    static void Main()
    {
        var guid = Guid.Parse("06bb9310-3f8b-4010-b006-c98abbb715cc");
        Console.WriteLine("ToString: " + guid.ToString());
        Console.WriteLine("Json: " + JsonSerializer.Serialize(guid));
    }
}
