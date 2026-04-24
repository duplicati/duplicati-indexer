using System;
var guid1 = new Guid("06bb9310-3f8b-4010-b006-c98abbb715cc");
var guid2 = new Guid("1093bb06-8b3f-1040-b006-c98abbb715cc");
Console.WriteLine($"guid1: {guid1}");
Console.WriteLine($"guid2: {guid2}");
var bytes1 = guid1.ToByteArray();
var bytes2 = guid2.ToByteArray();
Console.WriteLine($"bytes1 hex: {Convert.ToHexString(bytes1)}");
Console.WriteLine($"bytes2 hex: {Convert.ToHexString(bytes2)}");
