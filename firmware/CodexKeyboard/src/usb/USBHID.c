// Non-blocking transport for the vendor-defined HID collection.
#include "USBHID.h"

#include "USBconstant.h"
#include "USBhandler.h"

extern __xdata __at(EP1_ADDR) uint8_t Ep1Buffer[];

static volatile __xdata uint8_t usb_in_busy_s;
static volatile __xdata uint8_t usb_in_complete_s;
static volatile __xdata uint8_t usb_out_pending_s;
static volatile __xdata uint8_t usb_out_length_s;

void USBInit(void)
{
  USBDeviceCfg();
  USBDeviceEndPointCfg();
  USB_transport_reset();
  USBDeviceIntCfg();
}

void USB_EP1_IN(void)
{
  UEP1_T_LEN = 0;
  UEP1_CTRL = (UEP1_CTRL & ~MASK_UEP_T_RES) | UEP_T_RES_NAK;
  usb_in_busy_s = 0;
  usb_in_complete_s = 1;
}

void USB_EP1_OUT(void)
{
  if (U_TOG_OK)
  {
    usb_out_length_s = USB_RX_LEN;
    usb_out_pending_s = 1;
    UEP1_CTRL = (UEP1_CTRL & ~MASK_UEP_R_RES) | UEP_R_RES_NAK;
  }
}

uint8_t USB_send_report(const __xdata uint8_t *report)
{
  uint8_t sent = 0;
  uint8_t interrupts_enabled = EA;
  EA = 0;
  if (UsbConfig && !usb_in_busy_s)
  {
    for (uint8_t i = 0; i < HID_REPORT_LENGTH; i++)
    {
      Ep1Buffer[64 + i] = report[i];
    }
    UEP1_T_LEN = HID_REPORT_LENGTH;
    usb_in_busy_s = 1;
    usb_in_complete_s = 0;
    UEP1_CTRL = (UEP1_CTRL & ~MASK_UEP_T_RES) | UEP_T_RES_ACK;
    sent = 1;
  }
  EA = interrupts_enabled;
  return sent;
}

uint8_t USB_receive_report(__xdata uint8_t *report)
{
  uint8_t length = 0;
  uint8_t interrupts_enabled = EA;
  EA = 0;
  if (usb_out_pending_s)
  {
    length = usb_out_length_s;
    if (length <= HID_REPORT_LENGTH)
    {
      for (uint8_t i = 0; i < length; i++)
      {
        report[i] = Ep1Buffer[i];
      }
    }
    usb_out_pending_s = 0;
    UEP1_CTRL = (UEP1_CTRL & ~MASK_UEP_R_RES) | UEP_R_RES_ACK;
  }
  EA = interrupts_enabled;
  return length;
}

uint8_t USB_report_completed(void)
{
  return usb_in_complete_s;
}

void USB_transport_reset(void)
{
  usb_in_busy_s = 0;
  usb_in_complete_s = 0;
  usb_out_pending_s = 0;
  usb_out_length_s = 0;
  UEP1_T_LEN = 0;
}
