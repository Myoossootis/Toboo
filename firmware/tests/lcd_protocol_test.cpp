#include "../uno_lcd/LcdFace.h"
#include <assert.h>
#include <stdio.h>

LcdProtocol::Result send(LcdProtocol& p, const char* line, uint32_t now) {
  LcdProtocol::Result result = LcdProtocol::None;
  for (; *line; ++line) result = p.feed(*line, now);
  return result;
}
int main() {
  LcdProtocol p;
  assert(p.expression == 7);
  assert(send(p, "E,happy,400\n", 0) == LcdProtocol::Rejected);
  assert(send(p, "TOBOO_LCD?\r\n", 1) == LcdProtocol::Hello);
  for (uint8_t i = 0; i < 8; ++i) {
    char line[40]; snprintf(line, sizeof(line), "E,%s,400\n", LcdProtocol::label(i));
    assert(send(p, line, 2) == LcdProtocol::Updated);
    assert(p.expression == i);
  }
  assert(send(p, "E,happy,400\n", 100) == LcdProtocol::Updated);
  const char* invalid[] = {"E,happy,501\n", "E,happy,0\n", "E,happy,-1\n", "E,happy,12x\n",
    "E,happy,20,0\n", "E,Happy,100\n", "E,happy,\n", "MOVE,180\n",
    "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxE,sad,100\n"};
  for (const char* text : invalid) assert(send(p, text, 200) == LcdProtocol::Rejected);
  assert(!p.expire(499)); assert(p.expire(500)); assert(p.expression == 7);
  send(p, "E,hap", 600);
  assert(send(p, "py,400\n", 701) == LcdProtocol::Rejected);
  assert(send(p, "E,sad,200\n", 702) == LcdProtocol::Updated);
  assert(p.expression == 5);
  assert(send(p, "TOBOO_LCD?\n", 703) == LcdProtocol::Hello); assert(p.expression == 7);
  send(p, "E,happy,200\n", UINT32_MAX - 100);
  assert(!p.expire(98)); assert(p.expire(99));

  LcdProtocol role;
  assert(role.animated && role.role == LcdRole::Idle);
  assert(send(role, "R,thinking,1200\n", 0) == LcdProtocol::Rejected);
  assert(send(role, "TOBOO_LCD?\n", 5) == LcdProtocol::Hello);
  assert(send(role, "LCD_CAPS?\n", 5) == LcdProtocol::Capabilities);
  assert(send(role, "R,thinking,1200\n", 10) == LcdProtocol::RoleUpdated);
  assert(role.role == LcdRole::Thinking && role.roleStartedAt == 10);
  assert(send(role, "R,thinking,1200\n", 200) == LcdProtocol::RoleUpdated);
  assert(role.roleStartedAt == 10);
  assert(send(role, "R,neutral,1200\n", 220) == LcdProtocol::Rejected);
  assert(send(role, "R,happy,1501\n", 220) == LcdProtocol::Rejected);
  assert(!role.expire(1399) && role.expire(1400));
  assert(role.role == LcdRole::Idle && role.roleStartedAt == 1400);
  assert(send(role, "R,responding,200\n", UINT32_MAX - 100) == LcdProtocol::RoleUpdated);
  assert(!role.expire(98) && role.expire(99));
  assert(send(role, "E,happy,400\n", 100) == LcdProtocol::Updated);
  assert(!role.animated && role.expression == 3);
  assert(send(role, "R,idle,1200\n", 110) == LcdProtocol::RoleUpdated);
  assert(role.animated && role.roleStartedAt == 110 && role.expression == 7);

  LcdFace::Frame open, blink, talking, resting, glanceLeft, glanceRight, happy, attentive;
  LcdFace::frameAt(LcdRole::Idle, 0, open);
  LcdFace::frameAt(LcdRole::Idle, 3550, blink);
  assert(memcmp(open, blink, 32) != 0);
  LcdFace::frameAt(LcdRole::Responding, 0, resting);
  LcdFace::frameAt(LcdRole::Responding, 140, talking);
  assert(memcmp(resting, talking, 32) == 0 && memcmp(resting[4], talking[4], 16) != 0);
  LcdFace::frameAt(LcdRole::Thinking, 0, glanceLeft);
  LcdFace::frameAt(LcdRole::Thinking, 1000, glanceRight);
  assert(memcmp(glanceLeft, glanceRight, 32) != 0);
  LcdFace::frameAt(LcdRole::Happy, 0, happy);
  LcdFace::frameAt(LcdRole::Attentive, 0, attentive);
  assert(memcmp(happy, open, 32) != 0 && memcmp(attentive, open, 32) != 0);
  puts("PASS: LCD protocol, legacy compatibility, autonomous role playback, animation frames, expiry and millis wrap.");
}
