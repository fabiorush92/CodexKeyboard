#ifndef USER_USB_RAM
#error "USB RAM is required. Select the second USB setting for the CH552 board."
#endif

#include "src/input.h"
#include "src/led.h"
#include "src/neo/neo.h"
#include "src/protocol.h"
#include "src/usb/USBHID.h"
#include "src/util.h"

#define PIN_BUTTON_1 11
#define PIN_BUTTON_2 17
#define PIN_BUTTON_3 16
#define PIN_KNOB_BUTTON 33
#define PIN_ENCODER_A 31
#define PIN_ENCODER_B 30

void setup()
{
  NEO_init();
  delay(10);
  NEO_clearAll();

  led_setup();
  protocol_setup();
  input_setup(PIN_BUTTON_1, PIN_BUTTON_2, PIN_BUTTON_3, PIN_KNOB_BUTTON,
              PIN_ENCODER_A, PIN_ENCODER_B);

  if (input_knob_pressed())
  {
    led_show_bootloader();
    BOOT_now();
  }

  USBInit();
}

void loop()
{
  uint32_t now = millis();
  input_update(now);
  protocol_update(now);
  led_update(now);
}
