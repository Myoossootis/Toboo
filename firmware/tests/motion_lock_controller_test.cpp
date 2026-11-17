#define TOBOO_ALL_SERVO_OUTPUT_LOCKED 1
#include "../esp32_single_servo/SingleServoController.h"
#include <assert.h>
#include <iostream>
#include <string>

struct Output : single_servo::Output {
  bool enabled = false;
  int frames = 0;
  bool enable(bool value) override { enabled = value; return true; }
  bool send(const char*, size_t) override { ++frames; return true; }
};
int main() {
  assert(!single_servo::validServoFrame("$A090#", 6));
  assert(!single_servo::validServoFrame("$B045#", 6));
  assert(!single_servo::validServoFrame("$C060#", 6));
  Output output;
  single_servo::Controller controller(output); controller.start(1, 2);
  char reply[192];
  controller.command("HELLO", 0, reply, sizeof(reply));
  assert(std::string(reply).find("0.5.3-motion-locked") != std::string::npos);
  for (const char* verb : {"ARM_S1", "ARM_S2", "ARM_S3", "CENTER_S2", "MOVE_S2", "ARM_BOTH", "INIT_BOTH", "ARM_FOLLOW", "FOLLOW_BOTH"}) {
    const auto line = std::string(verb) + "," + controller.session() + ",1";
    controller.command(line.c_str(), 0, reply, sizeof(reply));
    assert(std::string(reply).find("ERR,ALL_OUTPUT_LOCKED,") == 0);
    assert(!output.enabled && output.frames == 0);
  }
  for (const char* query : {"STATUS", "STATUS_S2", "DISARM"}) {
    controller.command(query, 0, reply, sizeof(reply));
    assert(std::string(reply).find("ERR,") != 0);
    assert(!output.enabled && output.frames == 0);
  }
  std::cout << "PASS: all actuator outputs locked; status and DISARM remain available\n";
}
