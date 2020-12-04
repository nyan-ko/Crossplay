using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TShockAPI;
using System.IO;
using Terraria;
using TerrariaApi.Server;

namespace Crossplay
{
    [ApiVersion(2, 1)]
    public class Crossplay : TerrariaPlugin
    {
        public Crossplay(Main g) : base(g)
        {

        }

        public bool[] Mobile { get; private set; } = new bool[256];

        public override void Initialize()
        {
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
            ServerApi.Hooks.NetSendBytes.Register(this, OnSendBytes);
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

        private void OnSendBytes(SendBytesEventArgs args)
        {
            var tplr = TShock.Players[args.Socket.Id];
            if (tplr == null || !Mobile[args.Socket.Id])
                return;

            var type = (PacketTypes)args.Buffer[2];
            switch (type)
            {
                case PacketTypes.TileSendSquare:
                    ushort size;

                    using (var reader = new BinaryReader(new MemoryStream(args.Buffer, 0, args.Buffer.Length)))
                    {
                        reader.BaseStream.Position += 3;
                        size = reader.ReadUInt16();
                    }

                    size &= 32768;  // so mobile will always check TileChangeType, no other changes from 1405 to 1412 besides this

                    Buffer.BlockCopy(BitConverter.GetBytes(size), 0, args.Buffer, 3, 2);
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
    }
}
