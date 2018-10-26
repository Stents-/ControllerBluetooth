using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace GloveController
{
    public struct InputPacket {
        public ushort x;
        public ushort y;
        public bool button1;
        public bool button2;
        //public ushort battery;
    };

    public struct OutputPacket {
        public bool restart; // Restart flag
        public ushort hapticStr; // Haptic motor strength
        public ushort hapticDur; // Haptic motor duration
    }

    public class GloveController {
        public delegate void OnConnectedEvent();
        public delegate void OnDisconnectedEvent();
        public delegate void OnDeviceFoundEvent();

        public event OnConnectedEvent OnConnected;
        public event OnDisconnectedEvent OnDisconnected;
        public event OnDeviceFoundEvent OnDeviceFound;
        // Deliminator bytes
        const byte START = 17;
        const byte END = 18;
        const byte ESCAPE = 19;

        const byte PACKET_LENGTH = 6; // Packet length in bytes
        const byte OUTPUT_LENGTH = 5; // Output packet length in bytes

        StreamSocket stream;
        DataReader rx; // Recieve
        DataWriter tx; // Transmit
        DeviceWatcher deviceWatcher;
        BluetoothDevice device;
        DeviceInformation devInfo;

        public bool isConnected {
            get { return (device != null && device.ConnectionStatus == BluetoothConnectionStatus.Connected); }
        }

        public GloveController() {

            deviceWatcher = DeviceInformation.CreateWatcher("", null, DeviceInformationKind.AssociationEndpoint);
            deviceWatcher.Added += DeviceWatcher_Added;
        }

        public void StartConnection() {
            if (!isConnected) {
                // Start the watcher.
                deviceWatcher.Start();
            } else {
                throw new System.InvalidOperationException("Controller already connected");
            }
        }

        public async Task<InputPacket?> GetInputAsync() {
            if (isConnected) {
                uint loaded; // Number of bytes loaded in

                // Attempt to load in all the data, 512 bytes at a time
                do {
                    loaded = await rx.LoadAsync(512);
                } while (loaded >= 512);

                // Return the parsed packet from the data
                return ParsePacket();
            } else {
                throw new System.InvalidOperationException("Controller not connected");
            }
        }

        public void Pulse(ushort strength, ushort duration) {
            if (isConnected) {
                OutputPacket pack = new OutputPacket();
                pack.restart = false;
                pack.hapticStr = strength;
                pack.hapticDur = duration;

                Compose(pack);
                tx.StoreAsync();
            } else {
                throw new System.InvalidOperationException("Controller not connected");
            }
        }

        public void Restart() {
            if (isConnected) {
                OutputPacket pack = new OutputPacket();
                pack.restart = true;
                pack.hapticStr = 0;
                pack.hapticDur = 0;

                Compose(pack);
                tx.StoreAsync();
            } else {
                throw new System.InvalidOperationException("Controller not connected");
            }
        }

        private void DeviceWatcher_Added(DeviceWatcher d, DeviceInformation _devInfo) {
            Debug.WriteLine(_devInfo.Name);
            if (_devInfo.Name == "AetherController") {

                deviceWatcher.Stop();
                devInfo = _devInfo;
                OnDeviceFound();
            }
        }

        private void PairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args) {
            args.Accept("1234");
        }

        public async void Connect() {

            // Repair the device if needed
            if (!devInfo.Pairing.IsPaired) {
                Debug.WriteLine("Pairing...");

                DeviceInformationCustomPairing customPairing = devInfo.Pairing.Custom;
                customPairing.PairingRequested += PairingRequested;
                DevicePairingResult result = await customPairing.PairAsync(DevicePairingKinds.ProvidePin, DevicePairingProtectionLevel.None);
                customPairing.PairingRequested -= PairingRequested;

                Debug.WriteLine("Pair status: " + result.Status);
            } else {
                Debug.WriteLine("Already Paired");
            }

            // Get the actual device
            try {
                device = await BluetoothDevice.FromIdAsync(devInfo.Id);
                device.ConnectionStatusChanged += ConnectionStatusChanged;
            } catch (Exception ex) {
                Debug.WriteLine("Bluetooth Not Available");
                return;
            }

            //try {
            var services = await device.GetRfcommServicesAsync();
            if (services.Services.Count > 0) {

                var service = services.Services[0];
                stream = new StreamSocket();
                //try {
                await stream.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName);
                //} catch (Exception ex) {
                //Debug.WriteLine("Could not connect to device");
                //}
                Debug.WriteLine("Stream Connected");
                rx = new DataReader(stream.InputStream);
                rx.InputStreamOptions = InputStreamOptions.Partial;
                tx = new DataWriter(stream.OutputStream);
                
                OnConnected();
            }
            /*} catch (Exception ex) {
                Debug.WriteLine("Failed to get services");
                return;
            }*/


        }

        private void ConnectionStatusChanged(BluetoothDevice dev, object e) {
            switch (dev.ConnectionStatus) {
                case BluetoothConnectionStatus.Connected: {
                        Debug.WriteLine("Connected");
                        break;
                    }
                case BluetoothConnectionStatus.Disconnected: {
                        Debug.WriteLine("Lost Connection; Reconnecting...");

                        OnDisconnected();
                        /*device = null;

                        stream.Dispose();
                        stream = null;

                        rx.Dispose();
                        rx = null;

                        tx.Dispose();
                        tx = null;*/

                        // Attempt to reconnect
                        //StartConnection();

                        break;
                    }
            }
        }

        private InputPacket? ParsePacket() {
            if (rx.UnconsumedBufferLength > 0) {
                byte[] buf = new byte[rx.UnconsumedBufferLength];
                rx.ReadBytes(buf);

                int n = 0;          // Number of bytes in current packet
                int sInd = -1;      // Index of last START byte
                bool lit = false;  // Indicates whether the last byte was an ESCAPE
                int lastInd = -1;       // Start point of the last valid index
                for (int i = 0; i < buf.Length; i++) {
                    if (sInd < 0) {
                        if (buf[i] == START) {
                            sInd = i;
                            n = 0;
                            lit = false;
                        }
                    } else if (!lit) {
                        if (buf[i] == START) {
                            // ERROR, restart
                            sInd = i;
                            n = 0;
                            lit = false;
                        } else if (buf[i] == ESCAPE) {
                            // If escape, skip this character
                            lit = true;
                        } else if (buf[i] == END) {
                            // Check if the byte number is right
                            if (n == PACKET_LENGTH) {
                                // Valid packet
                                lastInd = sInd;
                                sInd = i;
                                n = 0;
                                lit = false;
                            } else {
                                // Invalid Packet
                                // Start looking for another packet
                                sInd = -1;
                                n = 0;
                                lit = false;
                            }
                        } else {
                            n++;
                        }
                    } else {
                        lit = false;
                        n++;
                        if (buf[i] != START && buf[i] != END && buf[i] != ESCAPE) {
                            // The controller should never set a non-special byte after an escape
                            sInd = -1;
                            n = 0;
                        }
                    }
                }

                if (lastInd >= 0) {

                    byte[] packet = new byte[6];
                    int ind = 0;

                    lit = false; // Interpret the next bit literally
                    for (int i = lastInd + 1; i < buf.Length; i++) {
                        if (!lit) {
                            if (buf[i] == ESCAPE) {
                                // If escape occured, next byte is literal
                                lit = true;
                            } else if (buf[i] == END) {
                                // End of packet
                                break;
                            } else {
                                //text += buf[i] + " ";
                                packet[ind] = buf[i];
                                ind++;
                            }
                        } else {
                            //text += buf[i] + " ";
                            packet[ind] = buf[i];
                            ind++;

                            lit = false;
                        }
                    }

                    InputPacket pack = new InputPacket();

                    pack.x = (ushort)(packet[0] | (packet[1] << 8));
                    pack.y = (ushort)(packet[2] | (packet[3] << 8));
                    pack.button1 = Convert.ToBoolean(packet[4]);
                    pack.button2 = Convert.ToBoolean(packet[5]);

                    return pack;
                }
            }
            return null;
        }

        private void Compose(OutputPacket pack) {
            // Convert the packet to a byte array
            byte[] buf = new byte[OUTPUT_LENGTH];
            buf[0] = (byte)((pack.restart) ? 1 : 0);
            buf[1] = (byte)((pack.hapticStr & 0xFF00) >> 8);
            buf[2] = (byte)(pack.hapticStr & 0x00FF);
            buf[3] = (byte)((pack.hapticDur & 0xFF00) >> 8);
            buf[4] = (byte)(pack.hapticDur & 0x00FF);

            int sz = buf.Length + 2; // Size of the actual data we send

            // Find out how many extra bytes to send
            foreach (byte b in buf) {
                if (b == START || b == END || b == ESCAPE) {
                    sz++;
                }
            }

            // Allocate data to send
            byte[] data = new byte[sz];
            data[0] = START;
            data[sz - 1] = END;

            // Fill the data
            int ind = 1;
            foreach (byte b in buf) {
                if (b == START || b == END || b == ESCAPE) {
                    // If a byte is a delim char, send an escape code
                    data[ind] = ESCAPE;
                    ind++;
                }
                data[ind] = b;
                ind++;
            }

            // Finally, write the data to the sream
            tx.WriteBytes(data);
        }
    }
}
