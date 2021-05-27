using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TShockAPI;
using System.IO;
using Terraria;
using TerrariaApi.Server;
using Terraria.ID;

namespace Crossplay
{
    [ApiVersion(2, 1)]
    public class Crossplay : TerrariaPlugin
    {
        public Crossplay(Main g) : base(g)
        {

        }

        public const int MOBILE_MAX_NPC_TYPES = 662;
        public const int MOBILE_MAX_ITEM_TYPES = 5044;

        public bool[] Mobile { get; private set; } = new bool[256];

        public override void Initialize()
        {
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
            ServerApi.Hooks.NetSendBytes.Register(this, OnSendBytes);
            ServerApi.Hooks.NetSendData.Register(this, OnSendData);

           
        }

        private void OnGetData(GetDataEventArgs args)
        {
            switch (args.MsgID)
            {
                case PacketTypes.ConnectRequest:
                    byte[] buffer = args.Msg.readBuffer;
                    int last = args.Index + args.Length - 2;

                    // Terraria230 -> Terraria234
                    if (buffer[last] == '0')
                    {
                        buffer[last] = (byte)'4';
                        Mobile[args.Msg.whoAmI] = true;
                    }
                    break;
            }
        }

        private void OnSendData(SendDataEventArgs args)
        {
            //TShock.Log.ConsoleInfo(args.MsgId.ToString());

            // basically send the correct STS (send tile square) for mobile players based off the SendData() parameters
            // 1.4.1.2 clients read width and height separately, however 1.4.0.5 (mobile) only reads one dimension and uses it for both sides of a square           

            if (args.MsgId == PacketTypes.TileSendSquare)
            {
                args.Handled = true;

                int x = args.number;
                int y = (int)args.number2;
                int width = (int)args.number3;
                int height = (int)args.number4;
                TileChangeType type = (TileChangeType)args.number5;

                //TShock.Log.ConsoleInfo($"x:{x} y:{y} w:{width} h:{height}");

                int sqDim = Math.Min(width, height);  // get the smaller dimension for the length of the square side

                // todo maybe only serialize sts/str if only mobile players/pc players are on respectively
                byte[] mobileRaw = SerializeSTS(x, y, sqDim, type);
                byte[] raw = SerializeSTR(x, y, width, height, type);

                for (int p = 0; p < 256; p++)
                {
                    // just send the bytes directly to avoid a loop
                    var client = Netplay.Clients[p];

                    // copied from decompiled terraria, Terraria.NetMessage.SendData()
                    if (client.IsConnected() && client.SectionRange(Math.Max(width, height), x, y) && NetMessage.buffer[p].broadcast) 
                    {
                        if (Mobile[p])
                        {
                            NetMessage.buffer[p].spamCount++;
                            Main.ActiveNetDiagnosticsUI.CountSentMessage((int)args.MsgId, mobileRaw.Length);
                            client.Socket.AsyncSend(mobileRaw, 0, mobileRaw.Length, client.ServerWriteCallBack, null);
                        }
                        else
                        {
                            NetMessage.buffer[p].spamCount++;
                            Main.ActiveNetDiagnosticsUI.CountSentMessage((int)args.MsgId, raw.Length);
                            client.Socket.AsyncSend(raw, 0, raw.Length, client.ServerWriteCallBack, null);
                        }
                    }
                }
            }
        }

        #region STS/STR formatting
        private byte[] SerializeSTS(int x, int y, int sqDim, TileChangeType type) 
        {
            var byteStream = new MemoryStream();
            using (var writer = new BinaryWriter(byteStream))
            {
                // make room for the packet header (length: 2 bytes, followed by type: 1 byte)
                writer.BaseStream.Position += 2;
                writer.Write((byte)PacketTypes.TileSendSquare);

                ushort size = (ushort)((sqDim & 32767) | 32768);  // squish dimension size into 15 bits and raise flag for tilechangetype inclusion

                writer.Write(size);  // tile square dimensions, ushort
                writer.Write((byte)type);  // tile change type
                writer.Write((short)x);  // top left coords of square
                writer.Write((short)y);

                // write all the tiles
                for (int i = x; i < x + sqDim; i++)
                {
                    for (int j = y; j < y + sqDim; j++)
                    {
                        WriteTile(writer, i, j);
                    }
                }

                // write the packet length now that we have the position of the writer
                long length = writer.BaseStream.Position;
                writer.BaseStream.Position = 0;
                writer.Write((short)length);

                // return the raw byte array to be sent as a packet
                return byteStream.ToArray();
            }
        }

        private byte[] SerializeSTR(int x, int y, int width, int height, TileChangeType type)
        {
            var byteStream = new MemoryStream();
            using (var writer = new BinaryWriter(byteStream))
            {
                writer.BaseStream.Position += 2;
                writer.Write((byte)PacketTypes.TileSendSquare);

                writer.Write((short)x);  // top left coords of rect
                writer.Write((short)y);
                writer.Write((byte)width);
                writer.Write((byte)height);
                writer.Write((byte)type);

                for (int i = x; i < x + width; i++)
                {
                    for (int j = y; j < y + height; j++)
                    {
                        WriteTile(writer, i, j);
                    }
                }

                long length = writer.BaseStream.Position;
                writer.BaseStream.Position = 0;
                writer.Write((short)length);

                return byteStream.ToArray();
            }
        }

        private void WriteTile(BinaryWriter writer, int x, int y)
        {
            BitsByte data = 0;  // just stores states like tile active, wires, actuation, etc.
            BitsByte data2 = 0;
            byte color = 0;
            byte wallColor = 0;

            var tile = Main.tile[x, y];

            // squish all the tile data into the bit flags
            // copied straight from decompiled terraria (Terraria.NetMessage.SendData()) with some renaming
            data[0] = tile.active();
            data[2] = tile.wall > 0;  // dont ask why data[1] is skipped :(
            data[3] = tile.liquid > 0;
            data[4] = tile.wire();
            data[5] = tile.halfBrick();
            data[6] = tile.actuator();
            data[7] = tile.inActive();

            data2[0] = tile.wire2();
            data2[1] = tile.wire3();

            if (tile.active() && tile.color() > 0)
            {
                data2[2] = true;
                color = tile.color();
            }
            if (tile.wall > 0 && tile.wallColor() > 0)
            {
                data2[3] = true;
                wallColor = tile.wallColor();
            }

            data2 += (byte)(tile.slope() << 4);
            data2[7] = tile.wire4();

            writer.Write(data);
            writer.Write(data2);

            if (color > 0)
            {
                writer.Write(color);
            }
            if (wallColor > 0)
            {
                writer.Write(wallColor);
            }

            if (tile.active())
            {
                writer.Write(tile.type);
                if (Main.tileFrameImportant[(int)tile.type])
                {
                    writer.Write(tile.frameX);
                    writer.Write(tile.frameY);
                }
            }

            if (tile.wall > 0)
            {
                writer.Write(tile.wall);
            }
            if (tile.liquid > 0 && Main.netMode == 2)
            {
                writer.Write(tile.liquid);
                writer.Write(tile.liquidType());
            }
        }
        #endregion

        private void OnSendBytes(SendBytesEventArgs args)
        {
            var tplr = TShock.Players[args.Socket.Id];
            if (tplr == null || !Mobile[args.Socket.Id])
                return;

            var type = (PacketTypes)args.Buffer[2];
            switch (type)
            {
                case PacketTypes.NpcKillCount:
                {
                    using (var reader = new BinaryReader(new MemoryStream(args.Buffer, args.Offset, args.Count)))
                    {
                        var npcId = reader.ReadInt16();

                        if (npcId > MOBILE_MAX_NPC_TYPES)
                        {
                            args.Handled = true;
                            Array.Clear(args.Buffer, args.Offset, args.Count);
                        }
                    }
                }
                    break;

                case PacketTypes.LoadNetModule:
                {
                    using (var reader = new BinaryReader(new MemoryStream(args.Buffer, args.Offset, args.Count)))
                    {
                        var id = reader.ReadUInt16();
                        if (id == 4)  // beastiary
                        {
                            reader.BaseStream.Position += 1;
                            var npcId = reader.ReadInt16();
                            if (npcId > MOBILE_MAX_NPC_TYPES)
                            {
                                args.Handled = true;
                                Array.Clear(args.Buffer, args.Offset, args.Count);
                            }
                        }
                        else if (id == 5)  // journey dupe item unlocks
                        {
                            var itemId = reader.ReadInt16();
                            if (itemId > MOBILE_MAX_ITEM_TYPES)
                            {
                                args.Handled = true;
                                Array.Clear(args.Buffer, args.Offset, args.Count);
                            }
                        }
                    }
                }
                    break;
                case PacketTypes.PlayerSlot:
                case PacketTypes.ChestItem:
                case PacketTypes.TravellingMerchantInventory:
                case PacketTypes.UpdateItemDrop:
                    byte[] fixedData = HandleItemPackets(type, args.Buffer.Skip(3).ToArray());
                    Buffer.BlockCopy(fixedData, 0, args.Buffer, 3, fixedData.Length);
                    break;
            }
        }

        private byte[] HandleItemPackets(PacketTypes type, byte[] bytes)
        {
            const int MAX_MOBILE_ITEM_TYPES = 5044;

            if (type == PacketTypes.TravellingMerchantInventory)
            {
                for (int offset = 0; offset < 80; offset += 2)
                {
                    short netId = BitConverter.ToInt16(bytes, offset);
                    if (netId > MAX_MOBILE_ITEM_TYPES)
                    {
                        Buffer.BlockCopy(BitConverter.GetBytes((short)0), 0, bytes, offset, 2);
                    }
                }

                return bytes;
            }
            else
            {
                int offset = 0;
                switch (type)
                {
                    case PacketTypes.PlayerSlot:
                    case PacketTypes.ChestItem:
                        offset = 6;
                        break;
                    case PacketTypes.UpdateItemDrop:
                        offset = 22;
                        break;
                    default:
                        return bytes;
                }

                short netId = BitConverter.ToInt16(bytes, offset);
                if (netId > MAX_MOBILE_ITEM_TYPES)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes((short)0), 0, bytes, offset, 2);
                }

                return bytes;
            }
        }

        private bool IsMobile(int index) => Mobile[index];
    }
}
