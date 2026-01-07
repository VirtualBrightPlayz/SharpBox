// See https://aka.ms/new-console-template for more information
using System;

// Console.WriteLine("Hello, World!");
var box = new SharpBox();
box.AllowDefaults();
// box.AddRule("System.Console/System.Console.*");
box.AddRule("System.Private.CoreLib/System.Security.UnverifiableCodeAttribute");
await box.RunCSharpTextAsync($"""
System.Console.WriteLine("Hello, World!");
""");