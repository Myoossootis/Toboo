// SPDX-License-Identifier: GPL-3.0-or-later
// UNO + 5V I2C LCD1602: GND/GND, VCC/5V, SDA/A4, SCL/A5.
#include <Wire.h>
#include <hd44780.h>
#include <hd44780ioClass/hd44780_I2Cexp.h>
#include "LcdProtocol.h"

hd44780_I2Cexp lcd; // Detect backpack address and pin mapping; no assumed 0x27.
LcdProtocol protocol;
int lcdStatus = -1;
uint8_t drawn = 255;

bool row(uint8_t y, const char* text) {
  if (lcd.setCursor(0, y) != 0) return false;
  for (uint8_t i = 0; i < 16; ++i) {
    if (lcd.write(*text ? *text++ : ' ') != 1) return false;
  }
  return true;
}
void draw() {
  if (lcdStatus != 0 || drawn == protocol.expression) return;
  drawn = protocol.expression;
  if (!row(0, LcdProtocol::face(drawn)) || !row(1, drawn == 7 ? "No valid cue" : LcdProtocol::label(drawn))) lcdStatus = -100;
}
void setup() {
  Serial.begin(115200);
  Wire.begin();
  Wire.setWireTimeout(25000, true);
  lcdStatus = lcd.begin(16, 2);
  if (lcdStatus == 0) { lcd.backlight(); draw(); }
}
void loop() {
  if (protocol.expire(millis())) draw();
  while (Serial.available()) {
    const LcdProtocol::Result result = protocol.feed((char)Serial.read(), millis());
    if (result == LcdProtocol::Hello) {
      drawn = 255; draw();
      if (lcdStatus == 0) Serial.println(F("TOBOO_LCD,1,UNO,16,2"));
      else { Serial.print(F("TOBOO_LCD_ERROR,")); Serial.println(lcdStatus); }
    } else if (result == LcdProtocol::Updated) {
      draw();
      if (lcdStatus == 0) { Serial.print(F("OK,")); Serial.println(LcdProtocol::label(protocol.expression)); }
      else { Serial.print(F("TOBOO_LCD_ERROR,")); Serial.println(lcdStatus); }
    } else if (result == LcdProtocol::Status) {
      Serial.print(F("LCD_STATE,")); Serial.println(LcdProtocol::label(protocol.expression));
    } else if (result == LcdProtocol::Rejected) Serial.println(F("ERR"));
  }
}
