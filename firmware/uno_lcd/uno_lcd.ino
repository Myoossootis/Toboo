// SPDX-License-Identifier: GPL-3.0-or-later
// UNO + 5V I2C LCD1602: GND/GND, VCC/5V, SDA/A4, SCL/A5.
#include <Wire.h>
#include <hd44780.h>
#include <hd44780ioClass/hd44780_I2Cexp.h>
#include "LcdFace.h"
#include "LcdRange.h"

#ifndef TOBOO_LCD_ULTRASONIC
#define TOBOO_LCD_ULTRASONIC 0
#endif
const uint8_t triggerPin = 7, echoPin = 8;
uint32_t lastRangeAt = 0;
bool hasRanged = false;

hd44780_I2Cexp lcd; // Detect backpack address and pin mapping; no assumed 0x27.
LcdProtocol protocol;
int lcdStatus = -1;
uint8_t drawn = 255;
bool faceVisible = false;
LcdFace::Frame displayedGlyphs = {};
uint32_t lastAnimationDraw = 0;

bool row(uint8_t y, const char* text) {
  if (lcd.setCursor(0, y) != 0) return false;
  for (uint8_t i = 0; i < 16; ++i) {
    if (lcd.write(*text ? *text++ : ' ') != 1) return false;
  }
  return true;
}
bool drawFace(uint32_t now) {
  LcdFace::Frame glyphs;
  LcdFace::frameAt(protocol.role, now - protocol.roleStartedAt, glyphs);
  for (uint8_t slot = 0; slot < LcdFace::glyphCount; ++slot) {
    if (faceVisible && memcmp(glyphs[slot], displayedGlyphs[slot], 8) == 0) continue;
    if (lcd.createChar(slot, glyphs[slot]) != 0) return false;
  }
  memcpy(displayedGlyphs, glyphs, sizeof(glyphs));
  if (!faceVisible) {
    const uint8_t cells[2][16] = {
      {' ', ' ', ' ', 0, 1, ' ', ' ', ' ', ' ', ' ', ' ', 6, 7, ' ', ' ', ' '},
      {' ', ' ', ' ', 2, 3, ' ', ' ', 4, 5, ' ', ' ', 2, 3, ' ', ' ', ' '}
    };
    for (uint8_t y = 0; y < 2; ++y) {
      if (lcd.setCursor(0, y) != 0) return false;
      for (uint8_t cell : cells[y]) if (lcd.write(cell) != 1) return false;
    }
    faceVisible = true;
  }
  return true;
}
void draw(uint32_t now, bool immediate = false) {
  if (lcdStatus != 0) return;
  if (protocol.animated) {
    if (!immediate && faceVisible && (uint32_t)(now - lastAnimationDraw) < 40) return;
    lastAnimationDraw = now;
    if (!drawFace(now)) lcdStatus = -100;
  } else if (faceVisible || drawn != protocol.expression) {
    faceVisible = false;
    drawn = protocol.expression;
    if (!row(0, LcdProtocol::face(drawn)) || !row(1, drawn == 7 ? "No valid cue" : LcdProtocol::label(drawn))) lcdStatus = -100;
  }
}
void setup() {
  Serial.begin(115200);
#if TOBOO_LCD_ULTRASONIC
  pinMode(triggerPin, OUTPUT); digitalWrite(triggerPin, LOW); pinMode(echoPin, INPUT);
#endif
  Wire.begin();
  Wire.setWireTimeout(25000, true);
  lcdStatus = lcd.begin(16, 2);
  if (lcdStatus == 0) { lcd.backlight(); draw(millis(), true); }
}
void loop() {
  uint32_t now = millis();
  draw(now, protocol.expire(now));
  for (uint8_t count = 0; count < 32 && Serial.available(); ++count) {
    now = millis();
    const LcdProtocol::Result result = protocol.feed((char)Serial.read(), now);
    if (result == LcdProtocol::Hello) {
      draw(now, true);
      if (lcdStatus == 0) Serial.println(F("TOBOO_LCD,1,UNO,16,2"));
      else { Serial.print(F("TOBOO_LCD_ERROR,")); Serial.println(lcdStatus); }
    } else if (result == LcdProtocol::Capabilities) {
      Serial.println(F("LCD_CAPS,ROLE1"));
    } else if (result == LcdProtocol::Faces) {
      Serial.println(F("LCD_FACES,7"));
    } else if (result == LcdProtocol::RangeCapabilities) {
#if TOBOO_LCD_ULTRASONIC
      Serial.println(F("LCD_RANGE_CAPS,1"));
#else
      Serial.println(F("ERR"));
#endif
    } else if (result == LcdProtocol::Range) {
#if TOBOO_LCD_ULTRASONIC
      if (hasRanged && (uint32_t)(now - lastRangeAt) < 65) {
        Serial.println(F("LCD_RANGE,WAIT"));
      } else {
        hasRanged = true; lastRangeAt = now;
        digitalWrite(triggerPin, LOW); delayMicroseconds(2);
        digitalWrite(triggerPin, HIGH); delayMicroseconds(10); digitalWrite(triggerPin, LOW);
        const auto duration = pulseIn(echoPin, HIGH, 25000UL);
        Serial.print(F("LCD_RANGE,")); Serial.println(lcdDistanceMm(duration));
      }
#else
      Serial.println(F("ERR"));
#endif
    } else if (result == LcdProtocol::Updated || result == LcdProtocol::RoleUpdated) {
      draw(now, true);
      if (lcdStatus != 0) { Serial.print(F("TOBOO_LCD_ERROR,")); Serial.println(lcdStatus); }
      else if (result == LcdProtocol::RoleUpdated) {
        Serial.print(F("OK,R,")); Serial.println(LcdProtocol::roleLabel(protocol.role));
      } else { Serial.print(F("OK,")); Serial.println(LcdProtocol::label(protocol.expression)); }
    } else if (result == LcdProtocol::Status) {
      Serial.print(F("LCD_STATE,")); Serial.println(LcdProtocol::label(protocol.expression));
    } else if (result == LcdProtocol::Rejected) Serial.println(F("ERR"));
  }
}
