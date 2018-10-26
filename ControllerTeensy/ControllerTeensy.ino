#include <ADC.h>
#include <ADC_Module.h>
#include <RingBuffer.h>
#include <RingBufferDMA.h>

#define CPU_RESTART_ADDR (uint32_t *)0xE000ED0C
#define CPU_RESTART_VAL 0x5FA0004
#define Bluetooth Serial1

struct InputPacket {
    unsigned short x;
    unsigned short y;
    bool button1;
    bool button2;
    //unsigned short battery;

    bool operator!=(const InputPacket& rhs) {
        return (x != rhs.x) || (y != rhs.y) ||
               (button1 != rhs.button1) || (button2 != rhs.button2);
    }
};

struct OutputPacket {
    bool restart; // Restart flag
    unsigned short hapticStr; // Haptic motor strength
    unsigned short hapticDur; // Haptic motor duration
};

const int RATE = 10; // Delay between packets in ms

// Pins for the inputs
const int PIN_X = A11;
const int PIN_Y = A10;
const int PIN_B1 = 8;
const int PIN_B2 = 9;
const int PIN_HAPT = A9;

// Deliminator bytes
const char START = 17;
const char END = 18;
const char ESCAPE = 19;

InputPacket prevPack; // The last packet sent
IntervalTimer pulseTimer;

ADC *adc;

void setup() {
    // Initialize the Serial
    Serial.begin(9600); // USB Serial
    Bluetooth.begin(115200); // Bluetooth Serial

    adc = new ADC();

    // Set up the pins
    pinMode(PIN_B1, INPUT_PULLUP);
    pinMode(PIN_B2, INPUT_PULLUP);
    pinMode(PIN_X, INPUT);
    pinMode(PIN_Y, INPUT);
    pinMode(PIN_HAPT, OUTPUT);

    analogWriteResolution(10);

    pinMode(13, OUTPUT);
    digitalWrite(13, HIGH);
}

void reboot() {
    *CPU_RESTART_ADDR = CPU_RESTART_VAL;
}

void stopPulse() {
    analogWrite(PIN_HAPT, 0);
    pulseTimer.end();
}

OutputPacket parse(int avail) {
    char *buf = new char[avail];
    Bluetooth.readBytes(buf, avail);


    int n = 0;          // Number of bytes in current packet
    int sInd = -1;      // Index of last START byte
    bool lit = false;  // Indicates whether the last byte was an ESCAPE
    int lastInd = -1;       // Start point of the last valid index
    
    for (int i = 0; i < avail; i++) {
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
                if (n == 5) {
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
        char *packet = new char[5];
        int ind = 0;

        lit = false; // Interpret the next bit literally
        for (int i = lastInd + 1; i < avail; i++) {
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

        OutputPacket pack;
        pack.restart = static_cast<bool>(packet[0]);
        pack.hapticStr = static_cast<unsigned short>(packet[2] | (packet[1] << 8));
        pack.hapticDur = static_cast<unsigned short>(packet[4] | (packet[3] << 8));

        delete [] packet;

        return pack;
    }

    delete [] buf;
}

void loop() {
    // Create the packet
    InputPacket pack;
    pack.x = adc->analogRead(PIN_X, ADC_0);
    //delay(10);
    pack.y = adc->analogRead(PIN_Y, ADC_1);
    pack.button1 = !digitalRead(PIN_B1);
    pack.button2 = !digitalRead(PIN_B2);

    // Check for input
    int avail = Bluetooth.available();
    if (avail > 0) {
        OutputPacket outPack = parse(avail);

        if (outPack.restart) {
            reboot();
        }

        if (outPack.hapticStr > 0) {
            analogWrite(PIN_HAPT, outPack.hapticStr);
            pulseTimer.begin(stopPulse, outPack.hapticDur * 1000);
        }
    }

    // Only send if there are changes
    if (pack != prevPack) {
        prevPack = pack;

        // Cast the packet to its byte form
        char *data = reinterpret_cast<char *>(&pack);
        
        Bluetooth.write(START);
        for (int i = 0; i < 6; i++) {
            if (data[i] == START || data[i] == END || data[i] == ESCAPE) {
                // If the data happen to be a special code, send an escape code before
                Bluetooth.write(ESCAPE);
            }
            Bluetooth.write(data[i]);
        }
        Bluetooth.write(END);
    }
    delay(RATE);
}
