#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.SocketIO;

namespace Serval.Sample.SocketIO;

public sealed class ChatHandler : SocketIOHandler
{
    private readonly SocketIOServer _server;

    public ChatHandler(SocketIOServer server)
    {
        _server = server;
    }

    public override async ValueTask OnEventAsync(SocketIOEvent message, CancellationToken ct)
    {
        switch (message.Name)
        {
            case "join":
            {
                var room = message.Arguments.Get<string>(0);
                if (string.IsNullOrWhiteSpace(room) || room.Length > 64)
                {
                    await message.AcknowledgeAsync(new
                    {
                        error = "Choose a room name with 1 to 64 characters."
                    });
                    break;
                }

                message.Socket.Join(room);
                await message.AcknowledgeAsync(new
                {
                    room,
                    id = message.Socket.Id
                });
                break;
            }
            case "chat":
            {
                var room = message.Arguments.Get<string>(0)!;
                var text = message.Arguments.Get<string>(1);
                if (!message.Socket.Rooms.Contains(room) || string.IsNullOrWhiteSpace(text) || text.Length > 1000)
                {
                    await message.AcknowledgeAsync(new
                    {
                        error = "Join the room and send 1 to 1000 characters."
                    });
                    break;
                }

                await _server.BroadcastAsync(message.Socket.Namespace, "chat", [
                    new
                    {
                        from = message.Socket.Id,
                        room,
                        text
                    }
                ], room);
                await message.AcknowledgeAsync(new
                {
                    sent = true
                });
                break;
            }
            case "binary":
            {
                var bytes = message.Arguments.Get<byte[]>(0)!;
                await message.Socket.EmitAsync("binary", bytes);
                await message.AcknowledgeAsync(new
                {
                    bytes = bytes.Length
                });
                break;
            }
            case "question":
            {
                var reply = await message.Socket.EmitWithAcknowledgementAsync("question", ["Can you hear the server?"], ct: ct);
                await message.AcknowledgeAsync(reply.Get<string>(0));
                break;
            }
        }
    }
}
