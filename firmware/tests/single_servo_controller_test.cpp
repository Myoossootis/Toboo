#include "../esp32_single_servo/SingleServoController.h"
#include <assert.h>
#include <iostream>
#include <string>
#include <vector>

using single_servo::Controller;
using single_servo::State;
struct FakeOutput : single_servo::Output {
  bool enabled = false, failEnable = false, failSend = false;
  std::vector<std::string> sent;
  bool enable(bool value) override { enabled = value && !failEnable; return !value || enabled; }
  bool send(const char* frame, size_t length) override {
    assert(enabled && single_servo::validServoFrame(frame, length));
    if (failSend) return false;
    sent.push_back(std::string(frame, length)); return true;
  }
};
std::string raw(Controller& c, const std::string& line, uint32_t now) {
  char response[192]; c.command(line.c_str(), now, response, sizeof(response)); return response;
}
void expectRaw(Controller& c, const std::string& line, uint32_t now, const char* prefix) {
  const auto response = raw(c, line, now);
  if (response.find(prefix) != 0) {
    std::cerr << line << " -> " << response << "; expected " << prefix << '\n'; assert(false);
  }
}
void command(Controller& c, const char* verb, uint32_t seq, uint32_t now,
             const char* prefix = "ACK,", const char* value = nullptr) {
  std::string line = std::string(verb) + "," + c.session() + "," + std::to_string(seq);
  if (value) line += std::string(",") + value;
  expectRaw(c, line, now, prefix);
}
void tickThrough(Controller& c, uint32_t start, uint32_t end) {
  for (uint32_t offset = 10; offset <= end - start; offset += 10) c.tick(start + offset);
}

void pairRequest(Controller& c, const std::string& verb, uint32_t seq, uint32_t now,
                 const std::string& values = "", const char* expected = "ACK,") {
  expectRaw(c, verb + "," + c.session() + "," + std::to_string(seq) + values, now, expected);
}

int main() {
  assert(single_servo::validServoFrame("$B000#", 6));
  assert(single_servo::validServoFrame("$B180#", 6));
  FakeOutput calibrateOut;
  Controller calibrate(calibrateOut); calibrate.start(61, 62);
  command(calibrate, "ARM_S2", 1, 0);
  command(calibrate, "CENTER_S2", 2, 0, "ACK,", "45");
  command(calibrate, "MOVE_S2", 3, 0, "ERR,CALIBRATION_STEP", "43");
  assert(calibrate.frames == 1 && calibrate.target(1) == 45);
  uint32_t calibrateSeq = 2, calibrateTime = 0;
  for (int angle = 44; angle >= 0; --angle) {
    char value[4]; snprintf(value, sizeof(value), "%d", angle);
    command(calibrate, "MOVE_S2", ++calibrateSeq, calibrateTime, "ACK,", value);
    tickThrough(calibrate, calibrateTime, calibrateTime + 200);
    calibrateTime += 200;
    assert(calibrate.last(1) == angle);
  }
  command(calibrate, "MOVE_S2", ++calibrateSeq, calibrateTime, "ERR,NUMBER", "-1");
  command(calibrate, "MOVE_S2", calibrateSeq, calibrateTime, "ERR,CALIBRATION_STEP", "2");
  assert(calibrateOut.sent.back() == "$B000#");
  assert(calibrate.last(0) == -1 && calibrate.last(2) == -1);
  expectRaw(calibrate, "DISARM", calibrateTime, "ACK,DISARM");

  // Only manual S2 testing may use the replacement-servo extension to 90 degrees.
  FakeOutput extendedOut;
  Controller extended(extendedOut); extended.start(51, 52);
  command(extended, "ARM_S2", 1, 0);
  command(extended, "CENTER_S2", 2, 0, "ACK,", "45");
  command(extended, "MOVE_S2", 3, 0, "ERR,RANGE", "181");
  command(extended, "MOVE_S2", 3, 0, "ERR,CALIBRATION_STEP", "91");
  command(extended, "MOVE_S2", 3, 0, "ACK,", "90");
  uint32_t extendedSeq = 3;
  for (uint32_t now = 20; now <= 9000; now += 20) {
    if (now % 200 == 0) command(extended, "KEEP_S2", ++extendedSeq, now);
    else extended.tick(now);
  }
  assert(extended.last(1) == 90 && extended.frames == 46);
  for (size_t i = 0; i < extendedOut.sent.size(); ++i) {
    char expected[8]; snprintf(expected, sizeof(expected), "$B%03d#", 45 + int(i));
    assert(extendedOut.sent[i] == expected);
  }
  assert(extended.last(0) == -1 && extended.last(2) == -1);
  expectRaw(extended, "DISARM", 9000, "ACK,DISARM");
  ++extendedSeq;
  pairRequest(extended, "ARM_FOLLOW", extendedSeq, 9000, ",30,175,35,90", "ERR,REFERENCE");
  command(extended, "ARM_S2", extendedSeq, 9000);
  command(extended, "MOVE_S2", ++extendedSeq, 9000, "ACK,", "91");
  tickThrough(extended, 9000, 9200);
  assert(extended.last(1) == 91);
  command(extended, "MOVE_S2", ++extendedSeq, 9200, "ERR,CALIBRATION_STEP", "93");
  command(extended, "MOVE_S2", extendedSeq, 9200, "ACK,", "90");
  tickThrough(extended, 9200, 9400);
  assert(extended.last(1) == 90);
  extended.tick(9700);
  assert(extended.state == State::TimedOut && !extendedOut.enabled);

  // Reproduce the replacement-servo sequence: initialize, DISARM, re-arm, move.
  // These are generated UART bytes, not proof of driver reception or shaft angle.
  FakeOutput replacementOut;
  Controller replacement(replacementOut); replacement.start(41, 42);
  command(replacement, "ARM_S2", 1, 0);
  command(replacement, "CENTER_S2", 2, 0, "ACK,", "45");
  expectRaw(replacement, "DISARM", 10, "ACK,DISARM");
  tickThrough(replacement, 10, 810);
  command(replacement, "ARM_S2", 3, 810);
  assert(replacementOut.sent.size() == 1); // Re-arm must not repeat initialization.
  command(replacement, "MOVE_S2", 4, 810, "ACK,", "50");
  uint32_t replacementSeq = 4;
  for (uint32_t now = 830; now <= 1830; now += 20) {
    if ((now - 810) % 80 == 0) command(replacement, "KEEP_S2", ++replacementSeq, now);
    else replacement.tick(now);
  }
  const std::vector<std::string> replacementExpected = {
    "$B045#", "$B046#", "$B047#", "$B048#", "$B049#", "$B050#"};
  assert(replacementOut.sent == replacementExpected);
  assert(replacement.last(0) == -1 && replacement.last(2) == -1);
  assert(replacement.last(1) == 50 && replacement.target(1) == 50);
  expectRaw(replacement, "DISARM", 1830, "ACK,DISARM");
  tickThrough(replacement, 1830, 2830);
  assert(replacementOut.sent == replacementExpected && !replacementOut.enabled);

  // Isolated S3 replacement-servo test. Pair/follow still own S1/S2 only.
  assert(single_servo::validServoFrame("$C030#", 6));
  assert(single_servo::validServoFrame("$C090#", 6));
  FakeOutput thirdOut;
  Controller third(thirdOut); third.start(31, 32);
  expectRaw(third, "STATUS_S3", 0, "STATUS,DISARMED,");
  command(third, "ARM_S3", 1, 0);
  assert(thirdOut.sent.empty());
  command(third, "MOVE_S3", 2, 0, "ERR,REFERENCE", "30");
  command(third, "CENTER_S3", 2, 0, "ERR,FIRST_S3_RANGE", "90");
  command(third, "CENTER_S3", 2, 0, "ACK,", "60");
  command(third, "MOVE_S3", 3, 0, "ERR,RANGE", "29");
  command(third, "MOVE_S3", 3, 0, "ERR,RANGE", "91");
  command(third, "MOVE_S1", 3, 0, "ERR,AXIS", "100");
  command(third, "MOVE_S2", 3, 0, "ERR,AXIS", "50");
  command(third, "MOVE_S3", 3, 0, "ACK,", "30");
  uint32_t thirdSeq = 3;
  for (uint32_t now = 20; now <= 6000; now += 20) {
    if (now % 200 == 0) command(third, "KEEP_S3", ++thirdSeq, now);
    else third.tick(now);
  }
  assert(third.last(2) == 30 && third.frames == 31);
  command(third, "MOVE_S3", ++thirdSeq, 6000, "ACK,", "90");
  for (uint32_t now = 6020; now <= 18000; now += 20) {
    if (now % 200 == 0) command(third, "KEEP_S3", ++thirdSeq, now);
    else third.tick(now);
  }
  assert(third.last(2) == 90 && third.frames == 91);
  assert(third.last(0) == -1 && third.last(1) == -1);
  for (const auto& frame : thirdOut.sent) assert(frame[1] == 'C');
  third.tick(18500);
  assert(third.state == State::TimedOut && !thirdOut.enabled);
  assert(third.frames == 91);

  assert(single_servo::validServoFrame("$A030#", 6) && single_servo::validServoFrame("$A175#", 6));
  assert(single_servo::validServoFrame("$B035#", 6) && single_servo::validServoFrame("$B065#", 6));
  for (const char* frame : {"$A029#", "$A176#", "$B181#", "$C091#", "$C029#", "$D060#", "$A09x#", "$A090!"}) {
    assert(!single_servo::validServoFrame(frame, 6));
  }
  assert(!single_servo::validServoFrame("$A09", 4));

  FakeOutput out;
  Controller c(out); c.start(0x12345678, 0xaabbccdd);
  assert(!out.enabled && out.sent.empty() && c.last() == -1);
  const std::string originalSession = c.session();
  command(c, "CENTER_S1", 1, 0, "ERR,STATE");
  command(c, "ARM_S1", 1, 0);
  assert(out.enabled && out.sent.empty()); // ARM is NEVER a centering command.
  command(c, "MOVE_S1", 2, 0, "ERR,REFERENCE", "90");
  command(c, "CENTER_S1", 2, 0);
  assert(out.sent.size() == 1 && out.sent[0] == "$A090#");
  command(c, "CENTER_S1", 3, 0, "ERR,REFERENCE");
  command(c, "MOVE_S1", 3, 0, "ACK,", "92");
  tickThrough(c, 0, 190); assert(c.last() == 90 && out.sent.size() == 1);
  c.tick(200); assert(c.last() == 91 && out.sent.back() == "$A091#");
  command(c, "KEEP_S1", 4, 200);
  tickThrough(c, 200, 400); assert(c.last() == 92 && out.sent.back() == "$A092#");
  expectRaw(c, "PING", 400, "PONG");
  command(c, "KEEP_S1", 4, 400, "ERR,SEQUENCE");
  command(c, "ARM_S1", 5, 400, "ERR,STATE");
  tickThrough(c, 400, 690); c.tick(699); assert(c.state == State::Armed);
  c.tick(700); assert(c.state == State::TimedOut && !out.enabled);
  assert(originalSession != c.session() && c.target() == c.last());
  expectRaw(c, "ARM_S1," + originalSession + ",5", 710, "ERR,SESSION");
  command(c, "MOVE_S1", 5, 710, "ERR,STATE", "90");
  command(c, "ARM_S1", 5, 710);
  assert(out.sent.size() == 3); // Re-arm neither resumes nor repeats an old command.
  command(c, "MOVE_S1", 6, 710, "ACK,", "88");
  tickThrough(c, 710, 750);
  const auto oldFrames = out.sent.size();
  expectRaw(c, "DISARM", 750, "ACK,DISARM");
  tickThrough(c, 750, 1750);
  assert(out.sent.size() == oldFrames && c.target() == c.last() && !out.enabled);

  FakeOutput invalidOut;
  Controller invalid(invalidOut); invalid.start(1, 2);
  command(invalid, "ARM_S1", 1, 0);
  command(invalid, "CENTER_S1", 2, 0);
  for (const char* value : {"181", "999", "-90", "+90", "90.0", "", " 90", "90x", "4294967296"}) {
    command(invalid, "MOVE_S1", 3, 100, "ERR,", value);
  }
  expectRaw(invalid, "MOVE_S1," + std::string(invalid.session()) + ",4294967296,90", 100, "ERR,NUMBER");
  expectRaw(invalid, "MOVE_S1," + std::string(invalid.session()) + ",3,90,", 100, "ERR,FORMAT");
  expectRaw(invalid, "SET,3,900,900", 100, "ERR,FORMAT"); // Bench protocol cannot activate hardware.
  expectRaw(invalid, "$A180#", 100, "ERR,FORMAT");
  assert(invalid.sequence == 2 && invalidOut.sent.size() == 1);
  tickThrough(invalid, 100, 500); assert(invalid.state == State::TimedOut);

  FakeOutput wideOut;
  Controller wide(wideOut); wide.start(9, 10);
  command(wide, "ARM_S1", 1, 0); command(wide, "CENTER_S1", 2, 0);
  command(wide, "MOVE_S1", 3, 0, "ACK,", "80");
  uint32_t seq = 3;
  for (uint32_t now = 10; now <= 2000; now += 10) {
    if (now % 200 == 0) command(wide, "KEEP_S1", ++seq, now);
    else wide.tick(now);
    assert(wide.last() == 90 - int(now / 200));
  }
  assert(wideOut.sent.size() == 11 && wideOut.sent.back() == "$A080#");
  command(wide, "MOVE_S1", ++seq, 2000, "ACK,", "100");
  for (uint32_t now = 2010; now <= 6000; now += 10) {
    if (now % 200 == 0) command(wide, "KEEP_S1", ++seq, now);
    else wide.tick(now);
    assert(wide.last() == 80 + int((now - 2000) / 200));
  }
  assert(wideOut.sent.size() == 31 && wideOut.sent.back() == "$A100#");

  FakeOutput dualOut;
  Controller dual(dualOut); dual.start(11, 12);
  expectRaw(dual, "HELLO", 0, "HELLO,UAVTRACK_PANTILT,0.5.3,");
  command(dual, "ARM_S1", 1, 0);
  command(dual, "CENTER_S1", 2, 0);
  expectRaw(dual, "DISARM", 10, "ACK,DISARM");
  command(dual, "ARM_S2", 3, 20);
  assert(dualOut.sent.size() == 1 && dual.last(1) == -1 && !strcmp(dual.stateName(), "ARMED_S2"));
  command(dual, "MOVE_S2", 4, 20, "ERR,REFERENCE", "47");
  command(dual, "ARM_S1", 4, 20, "ERR,STATE");
  command(dual, "CENTER_S1", 4, 20, "ERR,AXIS");
  command(dual, "CENTER_S2", 4, 20, "ERR,FIRST_TILT_RANGE", "46");
  command(dual, "CENTER_S2", 4, 20, "ACK,", "45");
  assert(dualOut.sent.back() == "$B045#" && dual.last() == 90 && dual.last(1) == 45);
  command(dual, "MOVE_S2", 5, 20, "ERR,CALIBRATION_STEP", "34");
  command(dual, "MOVE_S2", 5, 20, "ERR,RANGE", "181");
  command(dual, "MOVE_S1", 5, 20, "ERR,AXIS", "80");
  command(dual, "MOVE_S2", 5, 20, "ACK,", "47");
  tickThrough(dual, 20, 210);
  assert(dualOut.sent.size() == 2);
  dual.tick(220); assert(dualOut.sent.back() == "$B046#");
  command(dual, "KEEP_S2", 6, 220);
  tickThrough(dual, 220, 420); assert(dualOut.sent.back() == "$B047#");
  command(dual, "MOVE_S2", 7, 420, "ACK,", "45");
  tickThrough(dual, 420, 900);
  command(dual, "KEEP_S1", 8, 900, "ERR,AXIS");
  dual.tick(920); assert(dual.state == State::TimedOut && !dualOut.enabled);
  assert(dual.last() == 90 && dual.last(1) == 45 && dual.target(1) == 45 && dualOut.sent.size() == 6);
  command(dual, "ARM_S1", 8, 930);
  command(dual, "MOVE_S1", 9, 930, "ACK,", "91");
  tickThrough(dual, 930, 1130);
  assert(dualOut.sent.back() == "$A091#" && dual.last(1) == 45);
  expectRaw(dual, "DISARM", 1140, "ACK,DISARM");
  dual.start(13, 14);
  assert(dual.last() == -1 && dual.last(1) == -1 && dual.frames == 0 && !dualOut.enabled);

  FakeOutput sweepOut;
  Controller sweep(sweepOut); sweep.start(17, 18);
  command(sweep, "ARM_S1", 1, 0);
  command(sweep, "CENTER_S1", 2, 0, "ERR,FIRST_PAN_RANGE", "45");
  assert(sweepOut.sent.empty());
  command(sweep, "CENTER_S1", 2, 0, "ACK,", "80");
  command(sweep, "MOVE_S1", 3, 0, "ACK,", "45");
  uint32_t sweepSeq = 3;
  for (uint32_t now = 10; now <= 7000; now += 10) {
    if (now % 200 == 0) command(sweep, "KEEP_S1", ++sweepSeq, now);
    else sweep.tick(now);
  }
  assert(sweepOut.sent.back() == "$A045#" && sweep.frames == 36 && sweep.last(1) == -1);
  command(sweep, "MOVE_S1", ++sweepSeq, 7000, "ACK,", "90");
  for (uint32_t now = 7010; now <= 16000; now += 10) {
    if (now % 200 == 0) command(sweep, "KEEP_S1", ++sweepSeq, now);
    else sweep.tick(now);
    assert(sweep.last() == 45 + int((now - 7000) / 200));
  }
  assert(sweepOut.sent.back() == "$A090#" && sweep.frames == 81 && sweep.last(1) == -1);

  FakeOutput nudgeOut;
  Controller nudge(nudgeOut); nudge.start(15, 16);
  command(nudge, "ARM_S2", 1, 0);
  command(nudge, "CENTER_S2", 2, 0, "ACK,", "45");
  expectRaw(nudge, "DISARM", 10, "ACK,DISARM");
  command(nudge, "ARM_S2", 3, 20);
  command(nudge, "MOVE_S2", 4, 20, "ACK,", "46");
  tickThrough(nudge, 20, 200);
  assert(nudge.frames == 2 && nudgeOut.sent.back() == "$B046#");
  expectRaw(nudge, "DISARM", 201, "ACK,DISARM");
  command(nudge, "ARM_S2", 5, 202);
  command(nudge, "MOVE_S2", 6, 202, "ACK,", "45");
  tickThrough(nudge, 202, 392);
  nudge.tick(399);
  assert(nudge.frames == 2 && nudge.last(1) == 46);
  nudge.tick(400);
  assert(nudge.frames == 3 && nudgeOut.sent.back() == "$B045#");
  expectRaw(nudge, "DISARM", 401, "ACK,DISARM");

  FakeOutput gapOut;
  Controller gap(gapOut); gap.start(3, 4);
  command(gap, "ARM_S1", 1, 0); command(gap, "CENTER_S1", 2, 0);
  command(gap, "MOVE_S1", 3, 0, "ACK,", "92");
  gap.tick(300); assert(gap.target() == 90 && gapOut.sent.size() == 1);
  tickThrough(gap, 300, 490); assert(gapOut.sent.size() == 1);
  command(gap, "KEEP_S1", 4, 500, "ERR,SESSION"); // Tick expires before processing command.
  assert(gap.state == State::TimedOut);

  FakeOutput failOut;
  Controller fail(failOut); fail.start(5, 6);
  failOut.failEnable = true;
  command(fail, "ARM_S1", 1, 0, "ERR,UART");
  assert(fail.state == State::Fault && !failOut.enabled && failOut.sent.empty());
  failOut.failEnable = false;
  command(fail, "ARM_S1", 2, 10);
  failOut.failSend = true;
  command(fail, "CENTER_S1", 3, 10, "ERR,UART");
  assert(fail.state == State::Fault && !failOut.enabled && fail.last() == -1);

  FakeOutput wrapOut;
  Controller wrap(wrapOut); wrap.start(7, 8);
  const uint32_t start = UINT32_MAX - 100;
  command(wrap, "ARM_S1", 1, start); command(wrap, "CENTER_S1", 2, start);
  command(wrap, "MOVE_S1", 3, start, "ACK,", "88");
  tickThrough(wrap, start, start + 400);
  assert(wrap.last() == 88 && wrapOut.sent[1] == "$A089#" && wrapOut.sent[2] == "$A088#");
  tickThrough(wrap, start + 400, start + 490);
  wrap.tick(start + 499); assert(wrap.state == State::Armed);
  wrap.tick(start + 500); assert(wrap.state == State::TimedOut && !wrapOut.enabled);
  command(wrap, "ARM_S1", UINT32_MAX, start + 510);
  command(wrap, "KEEP_S1", 0, start + 510, "ERR,SEQUENCE");
  const std::string preReset = wrap.session();
  wrap.start(9, 10);
  assert(wrap.state == State::Disarmed && wrap.last() == -1 && wrap.frames == 0 && !wrapOut.enabled);
  expectRaw(wrap, "ARM_S1," + preReset + ",1", 0, "ERR,SESSION");

  FakeOutput pairOut;
  Controller pair(pairOut); pair.start(21, 22);
  pairRequest(pair, "ARM_BOTH", 1, 0);
  assert(pairOut.sent.empty());
  pairRequest(pair, "MOVE_BOTH", 2, 0, ",103,50", "ERR,REFERENCE");
  pairRequest(pair, "INIT_BOTH", 2, 0, ",103,50");
  assert(pairOut.sent.size() == 2 && pairOut.sent[0] == "$A103#" && pairOut.sent[1] == "$B050#");
  pairRequest(pair, "MOVE_BOTH", 3, 0, ",29,50", "ERR,RANGE");
  pairRequest(pair, "MOVE_BOTH", 3, 0, ",175,181", "ERR,RANGE");
  pairRequest(pair, "MOVE_BOTH", 3, 0, ",133,65");
  uint32_t pairSeq = 3;
  for (uint32_t now = 10; now <= 1880; now += 10) {
    const int panBefore = pair.last(), tiltBefore = pair.last(1);
    if (now % 100 == 0) pairRequest(pair, "KEEP_BOTH", ++pairSeq, now);
    else pair.tick(now);
    assert(pair.last() - panBefore >= 0 && pair.last() - panBefore <= 1);
    assert(pair.last(1) - tiltBefore >= 0 && pair.last(1) - tiltBefore <= 1);
    // Both share one progress. Quantization permits at most 0.75 degree line error.
    assert(abs((pair.last() - 103) - 2 * (pair.last(1) - 50)) <= 1);
  }
  assert(pair.last() == 133 && pair.last(1) == 65);
  expectRaw(pair, "MOTION", 1880, "MOTION,IDLE");
  pairRequest(pair, "MOVE_BOTH", ++pairSeq, 1880, ",103,50");
  for (uint32_t now = 1890; now <= 2200; now += 10) {
    if (now % 100 == 0) pairRequest(pair, "KEEP_BOTH", ++pairSeq, now);
    else pair.tick(now);
  }
  const auto pairFrames = pair.frames;
  pair.tick(2700);
  assert(pair.state == State::TimedOut && !pairOut.enabled && pair.frames == pairFrames);
  pair.tick(2800); assert(pair.frames == pairFrames);
  assert(pair.target() == pair.last() && pair.target(1) == pair.last(1));
  pairRequest(pair, "ARM_BOTH", ++pairSeq, 2810);
  pairRequest(pair, "MOVE_BOTH", ++pairSeq, 2810, ",103,50");
  expectRaw(pair, "DISARM", 2820, "ACK,DISARM");
  pair.tick(4000); assert(pair.frames == pairFrames && !pairOut.enabled);
  pairRequest(pair, "ARM_BOTH", ++pairSeq, 4010);
  pairRequest(pair, "MOVE_BOTH", ++pairSeq, 4010, ",103,50");
  pair.tick(4120);
  assert(pair.state == State::TimedOut && !pairOut.enabled);
  assert(pair.target() == pair.last() && pair.target(1) == pair.last(1));

  FakeOutput followOut;
  Controller follow(followOut); follow.start(31, 32);
  pairRequest(follow, "ARM_FOLLOW", 1, 0, ",95,110,45,55", "ERR,REFERENCE");
  assert(!followOut.enabled && followOut.sent.empty());
  pairRequest(follow, "ARM_BOTH", 1, 0);
  pairRequest(follow, "INIT_BOTH", 2, 0, ",103,50");
  expectRaw(follow, "DISARM", 0, "ACK,DISARM");
  pairRequest(follow, "ARM_FOLLOW", 3, 0, ",30,175,0,181", "ERR,RANGE");
  pairRequest(follow, "ARM_FOLLOW", 3, 0, ",104,110,45,55", "ERR,REFERENCE");
  const auto initializedFrames = follow.frames;
  pairRequest(follow, "ARM_FOLLOW", 3, 0, ",95,110,45,55");
  assert(!strcmp(follow.stateName(), "ARMED_FOLLOW") && follow.frames == initializedFrames);
  pairRequest(follow, "FOLLOW_BOTH", 4, 0, ",11001,5000", "ERR,RANGE");
  pairRequest(follow, "FOLLOW_BOTH", 4, 0, ",10550,5100");
  pairRequest(follow, "MOVE_BOTH", 5, 0, ",103,50", "ERR,STATE");
  command(follow, "MOVE_S1", 5, 0, "ERR,AXIS", "104");
  uint32_t followSeq = 4;
  for (uint32_t now = 10; now <= 400; now += 10) {
    int beforePan = follow.last(), beforeTilt = follow.last(1);
    if (now % 80 == 0) pairRequest(follow, "FOLLOW_BOTH", ++followSeq, now, ",10550,5100");
    else follow.tick(now);
    assert(abs(follow.last() - beforePan) <= 1 && abs(follow.last(1) - beforeTilt) <= 1);
  }
  assert(follow.last() > 103 && follow.last(1) == 51);
  // Retarget while moving is accepted; there is no BUSY/restarted fixed-duration curve.
  pairRequest(follow, "FOLLOW_BOTH", ++followSeq, 400, ",9500,4500");
  for (uint32_t now = 410; now <= 800; now += 10) {
    if (now % 80 == 0) pairRequest(follow, "FOLLOW_BOTH", ++followSeq, now, ",9500,4500");
    else follow.tick(now);
  }
  assert(follow.last() < 105 && follow.last(1) < 51);
  pairRequest(follow, "HOLD_BOTH", ++followSeq, 800);
  auto holdFrames = follow.frames;
  tickThrough(follow, 800, 900);
  assert(follow.frames == holdFrames);
  pairRequest(follow, "FOLLOW_BOTH", ++followSeq, 900, ",11000,5500");
  tickThrough(follow, 900, 1050);
  holdFrames = follow.frames;
  for (uint32_t now = 1060; now <= 1300; now += 10) {
    if (now % 50 == 0) pairRequest(follow, "KEEP_FOLLOW", ++followSeq, now);
    else follow.tick(now);
  }
  assert(follow.frames == holdFrames && follow.state == State::Armed); // Heartbeats cannot revive expired targets.
  assert(follow.target() == follow.last() && follow.target(1) == follow.last(1));
  pairRequest(follow, "FOLLOW_BOTH", ++followSeq, 1300, ",11000,5500");
  follow.tick(1410); // Scheduler stall cancels rather than catches up.
  assert(follow.state == State::TimedOut && !followOut.enabled && follow.frames == holdFrames);
  auto staleSession = std::string(follow.session());
  expectRaw(follow, "DISARM", 1420, "ACK,DISARM");
  expectRaw(follow, "ARM_FOLLOW," + staleSession + ",999,95,110,45,55", 1420, "ERR,SESSION");

  single_servo::LineReader reader;
  using Event = single_servo::LineReader::Event;
  for (char ch : std::string("STATUS\r")) reader.feed(ch, 0);
  assert(reader.feed('\n', 199) == Event::Ready && !strcmp(reader.line(), "STATUS"));
  for (int i = 0; i < 120; ++i) reader.feed('x', 200);
  assert(reader.feed('\n', 201) == Event::Rejected);
  reader.feed('A', 300); reader.expire(500);
  assert(reader.feed('\n', 501) == Event::Rejected);
  reader.feed(0, 600); assert(reader.feed('\n', 600) == Event::Rejected);
  for (char ch : std::string("HELLO")) reader.feed(ch, 601);
  assert(reader.feed('\n', 601) == Event::Ready && !strcmp(reader.line(), "HELLO"));
  std::cout << "PASS: no startup/arm motion, independent S1/S2 references and limits, axis isolation, rate, timeout, replay, stalls, UART faults, reboot, rollover, framing\n";
}
