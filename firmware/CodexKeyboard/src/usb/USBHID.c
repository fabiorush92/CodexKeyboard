// Non-blocking transport for the vendor-defined HID collection.
#include "USBHID.h"

#include "USBconstant.h"
#include "USBhandler.h"

extern __xdata __at(EP1_ADDR) uint8_t Ep1Buffer[];

static volatile __xdata uint8_t usb_in_busy_s;
static volatile __xdata uint8_t usb_in_complete_s;
static volatile __xdata uint8_t usb_out_pending_s;
static volatile __xdata uint8_t usb_out_length_s;
static volatile __xdata uint8_t usb_reset_pending_s;
static volatile __xdata uint8_t usb_in_halted_s;
static volatile __xdata uint8_t usb_out_halted_s;

static void reset_transfer_state(void)
{
  usb_in_busy_s = 0;
  usb_in_complete_s = 0;
  usb_out_pending_s = 0;
  usb_out_length_s = 0;
  usb_reset_pending_s = 1;
  UEP1_T_LEN = 0;
}

static void update_endpoint_responses(void)
{
  uint8_t control = UEP1_CTRL & (bUEP_T_TOG | bUEP_R_TOG);
  control |= bUEP_AUTO_TOG;
  control |= usb_in_halted_s ? UEP_T_RES_STALL : UEP_T_RES_NAK;
  control |= usb_out_halted_s
                 ? UEP_R_RES_STALL
                 : (UsbConfig ? UEP_R_RES_ACK : UEP_R_RES_NAK);
  UEP1_CTRL = control;
}

void USBInit(void)
{
  USBDeviceCfg();
  USBDeviceEndPointCfg();
  USB_transport_reset();
  USBDeviceIntCfg();
}

void USB_EP1_IN(void)
{
  if (usb_in_halted_s)
  {
    return;
  }
  UEP1_T_LEN = 0;
  UEP1_CTRL = (UEP1_CTRL & ~MASK_UEP_T_RES) | UEP_T_RES_NAK;
  usb_in_busy_s = 0;
  usb_in_complete_s = 1;
}

void USB_EP1_OUT(void)
{
  if (!usb_out_halted_s && U_TOG_OK)
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
  if (UsbConfig && !usb_reset_pending_s && !usb_in_halted_s &&
      !usb_in_busy_s)
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
  if (!usb_reset_pending_s && !usb_out_halted_s && usb_out_pending_s)
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

uint8_t USB_claim_report_completion(void)
{
  uint8_t ready;
  uint8_t interrupts_enabled = EA;
  EA = 0;
  ready = usb_in_complete_s && !usb_reset_pending_s && !usb_in_halted_s &&
          !UIF_BUS_RST && !UIF_TRANSFER;
  if (ready)
  {
    usb_in_complete_s = 0;
  }
  EA = interrupts_enabled;
  return ready;
}

uint8_t USB_take_transport_reset(void)
{
  uint8_t pending;
  uint8_t interrupts_enabled = EA;
  EA = 0;
  pending = usb_reset_pending_s;
  usb_reset_pending_s = 0;
  EA = interrupts_enabled;
  return pending;
}

uint8_t USB_endpoint_halted(uint8_t endpoint_address)
{
  return endpoint_address == 0x81 ? usb_in_halted_s : usb_out_halted_s;
}

void USB_set_endpoint_halt(uint8_t endpoint_address)
{
  reset_transfer_state();
  if (endpoint_address == 0x81)
  {
    usb_in_halted_s = 1;
  }
  else
  {
    usb_out_halted_s = 1;
  }
  update_endpoint_responses();
}

void USB_clear_endpoint_halt(uint8_t endpoint_address)
{
  reset_transfer_state();
  if (endpoint_address == 0x81)
  {
    usb_in_halted_s = 0;
    UEP1_CTRL &= ~bUEP_T_TOG;
  }
  else
  {
    usb_out_halted_s = 0;
    UEP1_CTRL &= ~bUEP_R_TOG;
  }
  update_endpoint_responses();
}

void USB_cancel_pending_in(void)
{
  uint8_t interrupts_enabled = EA;
  EA = 0;
  usb_in_busy_s = 0;
  usb_in_complete_s = 0;
  UEP1_T_LEN = 0;
  if (!usb_in_halted_s)
  {
    UEP1_CTRL = (UEP1_CTRL & ~MASK_UEP_T_RES) | UEP_T_RES_NAK;
  }
  EA = interrupts_enabled;
}

void USB_transport_reset(void)
{
  reset_transfer_state();
  usb_in_halted_s = 0;
  usb_out_halted_s = 0;
  UEP1_CTRL = bUEP_AUTO_TOG | UEP_T_RES_NAK |
              (UsbConfig ? UEP_R_RES_ACK : UEP_R_RES_NAK);
}
