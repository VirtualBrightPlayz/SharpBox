using System;

var box = new SharpBox();
box.AllowDefaults();
box.AssemblyWhitelist.Add(typeof(Console).Assembly.GetName().Name);
box.AddRule("System.Console/*");
await box.RunCSharpTextAsync($"""
System.Console.WriteLine("Hello, World!");
""");
