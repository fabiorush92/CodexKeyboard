#pragma once

#include <stdint.h>

#define CK_REPORT_LENGTH 16
#define CK_REPORT_ID 0x01
#define CK_PROTOCOL_VERSION 0x01
#define CK_COMMAND_TIMEOUT_MS 250
#define CK_PING_INTERVAL_MS 1000
#define CK_HEARTBEAT_TIMEOUT_MS 3000
#define CK_MAX_EFFECT_PERIOD_10MS 6000

enum ck_message_t
{
  CK_MSG_GET_INFO = 0x01,
  CK_MSG_SET_SCENE = 0x02,
  CK_MSG_SET_RGB = 0x03,
  CK_MSG_PING = 0x04,
  CK_MSG_INPUT_EVENT = 0x81,
  CK_MSG_DEVICE_INFO = 0x82,
  CK_MSG_ACK = 0x83,
  CK_MSG_ERROR = 0x84
};

enum ck_control_t
{
  CK_CONTROL_BUTTON_1 = 0x01,
  CK_CONTROL_BUTTON_2 = 0x02,
  CK_CONTROL_BUTTON_3 = 0x03,
  CK_CONTROL_KNOB_BUTTON = 0x04,
  CK_CONTROL_ENCODER = 0x05
};

enum ck_input_t
{
  CK_INPUT_PRESS = 0x01,
  CK_INPUT_RELEASE = 0x02,
  CK_INPUT_ROTATE = 0x03
};

enum ck_scene_t
{
  CK_SCENE_COMPANION_ABSENT = 0x00,
  CK_SCENE_COMPANION_CONNECTED = 0x01,
  CK_SCENE_CODEX_UNAVAILABLE = 0x02,
  CK_SCENE_EFFORT_MEDIUM = 0x03,
  CK_SCENE_EFFORT_HIGH = 0x04,
  CK_SCENE_EFFORT_ULTRA = 0x05,
  CK_SCENE_ACTION_SUCCEEDED = 0x06,
  CK_SCENE_ACTION_FAILED = 0x07,
  CK_SCENE_TURN_ACTIVE = 0x08,
  CK_SCENE_WAITING_FOR_APPROVAL = 0x09,
  CK_SCENE_COMPLETED = 0x0A
};

enum ck_effect_t
{
  CK_EFFECT_SOLID = 0x00,
  CK_EFFECT_BLINK = 0x01,
  CK_EFFECT_BREATHE = 0x02
};

enum ck_error_t
{
  CK_ERROR_UNSUPPORTED_VERSION = 0x01,
  CK_ERROR_UNKNOWN_MESSAGE_TYPE = 0x02,
  CK_ERROR_WRONG_DIRECTION = 0x03,
  CK_ERROR_INVALID_PAYLOAD = 0x04,
  CK_ERROR_UNSUPPORTED_VALUE = 0x05
};

void protocol_setup(void);
void protocol_update(uint32_t now);
void protocol_set_button_state(uint8_t state);
void protocol_queue_input(uint8_t control, uint8_t kind, int8_t value,
                          uint8_t buttons);
