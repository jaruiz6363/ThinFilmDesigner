// Program.cs — stdio in, stdio out, and nothing on stdout that is not a message.
//
// A single stray Console.WriteLine anywhere in this process corrupts the stream and the client
// reports a parse error it cannot explain, so the entry point is deliberately this short and
// every diagnostic goes to stderr.

using ThinFilm.Mcp;

Console.OutputEncoding = System.Text.Encoding.UTF8;
new McpServer().Serve(Console.In, Console.Out);
return 0;
