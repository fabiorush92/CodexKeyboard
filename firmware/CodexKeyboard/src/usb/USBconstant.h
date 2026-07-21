#ifndef __CODEX_KEYBOARD_USB_CONSTANT_H__
#define __CODEX_KEYBOARD_USB_CONSTANT_H__

#include <stdint.h>
#include "include/ch5xx.h"
#include "include/ch5xx_usb.h"
#include "usbCommonDescriptors/StdDescriptors.h"
#include "usbCommonDescriptors/HIDClassCommon.h"

#define EP0_ADDR 0
#define EP1_ADDR 10
#define HID_IN_EPADDR 0x81
#define HID_OUT_EPADDR 0x01
#define HID_REPORT_LENGTH 16

typedef struct
{
  USB_Descriptor_Configuration_Header_t Config;
  USB_Descriptor_Interface_t HID_Interface;
  USB_HID_Descriptor_HID_t HID_Descriptor;
  USB_Descriptor_Endpoint_t HID_ReportINEndpoint;
  USB_Descriptor_Endpoint_t HID_ReportOUTEndpoint;
} USB_Descriptor_Configuration_t;

extern __code USB_Descriptor_Device_t DeviceDescriptor;
extern __code USB_Descriptor_Configuration_t ConfigurationDescriptor;
extern __code uint8_t ReportDescriptor[];
extern __code uint8_t LanguageDescriptor[];
extern __code uint16_t ProductDescriptor[];

#endif
