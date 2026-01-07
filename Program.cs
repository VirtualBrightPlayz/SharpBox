var box = new SharpBox();
box.AllowDefaults();
await box.RunCSharpTextAsync($"""
System.Console.WriteLine("Hello, World!");
""");
