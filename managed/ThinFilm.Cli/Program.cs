// Program.cs — the entry point, which does nothing but hand Console to something testable.
//
// Cli.Run takes its writers as arguments so the test suite can call it with a StringWriter and
// read what a user would have seen. A CLI whose only interface is a process is a CLI nobody tests.

using ThinFilm.Cli;

return CommandLine.Run(args, Console.Out, Console.Error);
