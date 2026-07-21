#include <Arduino.h>

#include "led.h"
#include "protocol.h"
#include "usb/USBHID.h"
#include "usb/USBhandler.h"

#define INPUT_QUEUE_SIZE 8
#define CK_CAPABILITIES 0x003F
#define CK_INPUT_FLAG_OVERFLOW 0x01

typedef struct
{
  uint8_t sequence;
  uint8_t control;
  uint8_t kind;
  int8_t value;
  uint8_t buttons;
} input_event_t;

static __xdata input_event_t input_queue_s[INPUT_QUEUE_SIZE];
static __xdata uint8_t report_s[CK_REPORT_LENGTH];
static uint8_t queue_head_s;
static uint8_t queue_tail_s;
static uint8_t queue_count_s;
static uint8_t input_sequence_s;
static uint8_t current_buttons_s;
static uint8_t overflow_pending_s;
static uint8_t response_pending_s;
static uint8_t session_active_s;
static uint32_t last_host_command_s;

static void clear_report(uint8_t type, uint8_t sequence)
{
  for (uint8_t i = 0; i < CK_REPORT_LENGTH; i++)
  {
    report_s[i] = 0;
  }
  report_s[0] = CK_REPORT_ID;
  report_s[1] = CK_PROTOCOL_VERSION;
  report_s[2] = type;
  report_s[3] = sequence;
}

static void clear_queue(void)
{
  queue_head_s = 0;
  queue_tail_s = 0;
  queue_count_s = 0;
  input_sequence_s = 0;
  overflow_pending_s = 0;
}

static void close_session(void)
{
  session_active_s = 0;
  response_pending_s = 0;
  clear_queue();
  led_show_absent();
}

static void make_error(uint8_t sequence, uint8_t failed_type, uint8_t error,
                       uint8_t detail)
{
  clear_report(CK_MSG_ERROR, sequence);
  report_s[4] = failed_type;
  report_s[5] = error;
  report_s[6] = detail;
  response_pending_s = 1;
}

static uint8_t first_nonzero(uint8_t start)
{
  for (uint8_t i = start; i < CK_REPORT_LENGTH; i++)
  {
    if (report_s[i])
    {
      return i;
    }
  }
  return 0xFF;
}

static void activate_session(uint32_t now)
{
  if (!session_active_s)
  {
    clear_queue();
    session_active_s = 1;
    led_set_scene(CK_SCENE_COMPANION_CONNECTED, CK_EFFECT_SOLID, 8, 0);
  }
  last_host_command_s = now;
}

static void make_ack(uint8_t sequence, uint8_t command)
{
  clear_report(CK_MSG_ACK, sequence);
  report_s[4] = command;
  response_pending_s = 1;
}

static void make_device_info(uint8_t sequence)
{
  clear_report(CK_MSG_DEVICE_INFO, sequence);
  report_s[4] = 1;
  report_s[5] = 0;
  report_s[6] = 0;
  report_s[7] = CK_CAPABILITIES & 0xFF;
  report_s[8] = CK_CAPABILITIES >> 8;
  report_s[9] = 4;
  report_s[10] = 1;
  report_s[11] = 3;
  report_s[12] = LED_MAX_COMPONENT;
  response_pending_s = 1;
}

static void process_command(uint8_t length, uint32_t now)
{
  if (length != CK_REPORT_LENGTH || report_s[0] != CK_REPORT_ID)
  {
    return;
  }

  uint8_t sequence = report_s[3];
  uint8_t type = report_s[2];
  if (report_s[1] != CK_PROTOCOL_VERSION)
  {
    make_error(sequence, type, CK_ERROR_UNSUPPORTED_VERSION,
               CK_PROTOCOL_VERSION);
    return;
  }
  if (type >= CK_MSG_INPUT_EVENT && type <= CK_MSG_ERROR)
  {
    make_error(sequence, type, CK_ERROR_WRONG_DIRECTION, 2);
    return;
  }
  if (type < CK_MSG_GET_INFO || type > CK_MSG_PING)
  {
    make_error(sequence, type, CK_ERROR_UNKNOWN_MESSAGE_TYPE, 2);
    return;
  }

  uint8_t invalid = 0xFF;
  uint16_t period;
  switch (type)
  {
  case CK_MSG_GET_INFO:
  case CK_MSG_PING:
    invalid = first_nonzero(4);
    break;
  case CK_MSG_SET_SCENE:
    invalid = first_nonzero(9);
    if (invalid == 0xFF && report_s[4] > CK_SCENE_COMPLETED)
    {
      make_error(sequence, type, CK_ERROR_UNSUPPORTED_VALUE, 4);
      return;
    }
    if (invalid == 0xFF && report_s[5] > CK_EFFECT_BREATHE)
    {
      make_error(sequence, type, CK_ERROR_UNSUPPORTED_VALUE, 5);
      return;
    }
    period = report_s[7] | ((uint16_t)report_s[8] << 8);
    if (invalid == 0xFF &&
        ((report_s[5] == CK_EFFECT_SOLID && period != 0) ||
         (report_s[5] != CK_EFFECT_SOLID &&
          (period == 0 || period > CK_MAX_EFFECT_PERIOD_10MS))))
    {
      make_error(sequence, type, CK_ERROR_UNSUPPORTED_VALUE, 7);
      return;
    }
    break;
  case CK_MSG_SET_RGB:
    invalid = first_nonzero(13);
    break;
  }

  if (invalid != 0xFF)
  {
    make_error(sequence, type, CK_ERROR_INVALID_PAYLOAD, invalid);
    return;
  }

  activate_session(now);
  switch (type)
  {
  case CK_MSG_GET_INFO:
    make_device_info(sequence);
    break;
  case CK_MSG_SET_SCENE:
    period = report_s[7] | ((uint16_t)report_s[8] << 8);
    led_set_scene(report_s[4], report_s[5], report_s[6], period);
    make_ack(sequence, type);
    break;
  case CK_MSG_SET_RGB:
    led_set_rgb(report_s + 4);
    make_ack(sequence, type);
    break;
  case CK_MSG_PING:
    make_ack(sequence, type);
    break;
  }
}

void protocol_setup(void)
{
  current_buttons_s = 0;
  response_pending_s = 0;
  session_active_s = 0;
  last_host_command_s = 0;
  clear_queue();
  led_show_absent();
}

void protocol_set_button_state(uint8_t state)
{
  current_buttons_s = state & 0x0F;
}

void protocol_queue_input(uint8_t control, uint8_t kind, int8_t value,
                          uint8_t buttons)
{
  current_buttons_s = buttons & 0x0F;
  if (!session_active_s)
  {
    return;
  }

  uint8_t sequence = input_sequence_s++;
  if (queue_count_s == INPUT_QUEUE_SIZE)
  {
    overflow_pending_s = 1;
    return;
  }

  input_event_t *event = &input_queue_s[queue_tail_s];
  event->sequence = sequence;
  event->control = control;
  event->kind = kind;
  event->value = value;
  event->buttons = current_buttons_s;
  queue_tail_s = (queue_tail_s + 1) & (INPUT_QUEUE_SIZE - 1);
  queue_count_s++;
}

void protocol_update(uint32_t now)
{
  if (!UsbConfig)
  {
    if (session_active_s || response_pending_s || queue_count_s)
    {
      close_session();
    }
    return;
  }

  if (session_active_s &&
      now - last_host_command_s >= CK_HEARTBEAT_TIMEOUT_MS)
  {
    close_session();
  }

  if (!response_pending_s)
  {
    uint8_t length = USB_receive_report(report_s);
    if (length)
    {
      process_command(length, now);
    }
  }

  if (response_pending_s)
  {
    if (USB_send_report(report_s))
    {
      response_pending_s = 0;
    }
    return;
  }

  if (!session_active_s || !queue_count_s)
  {
    return;
  }

  input_event_t *event = &input_queue_s[queue_head_s];
  clear_report(CK_MSG_INPUT_EVENT, event->sequence);
  report_s[4] = event->control;
  report_s[5] = event->kind;
  report_s[6] = event->value;
  report_s[7] = overflow_pending_s ? CK_INPUT_FLAG_OVERFLOW : 0;
  report_s[8] = overflow_pending_s ? current_buttons_s : event->buttons;
  if (USB_send_report(report_s))
  {
    overflow_pending_s = 0;
    queue_head_s = (queue_head_s + 1) & (INPUT_QUEUE_SIZE - 1);
    queue_count_s--;
  }
}
