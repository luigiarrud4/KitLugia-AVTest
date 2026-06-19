ARCH        ?= x86_64
TARGET      ?= kitlugia_shrink

CC       = gcc
LD       = ld
OBJCOPY  = objcopy

# Auto-detect gnu-efi: prefer system-wide, fallback to ./deps/
GNUEFI_SYS := /usr/include/efi
GNUEFI_LOCAL := ./deps/gnu-efi/inc
ifneq ($(wildcard $(GNUEFI_SYS)/efi.h),)
  GNUEFI_INC := $(GNUEFI_SYS)
  GNUEFI_ARCH_INC := $(GNUEFI_SYS)/$(ARCH)
  GNUEFI_LIB := /usr/lib
  GNUEFI_CRT0 := /usr/lib/crt0-efi-$(ARCH).o
  GNUEFI_LDS := /usr/lib/elf_$(ARCH)_efi.lds
else
  GNUEFI_INC := $(GNUEFI_LOCAL)
  GNUEFI_ARCH_INC := $(GNUEFI_LOCAL)/$(ARCH)
  GNUEFI_LIB := $(dir $(wildcard ./deps/gnu-efi/*/libefi.a))lib
  GNUEFI_CRT0 := ./deps/gnu-efi/$(ARCH)/gnuefi/crt0-efi-$(ARCH).o
  GNUEFI_LDS := ./deps/gnu-efi/gnuefi/elf_$(ARCH)_efi.lds
endif

OBJDIR   = obj
BINDIR   = bin

SRCS     = kitlugia_shrink.c gpt_lib.c disk_io.c
OBJS     = $(SRCS:%.c=$(OBJDIR)/%.o)
DEPS     = $(SRCS:%.c=$(OBJDIR)/%.d)

CFLAGS  = -I. -I$(GNUEFI_INC) -I$(GNUEFI_ARCH_INC) \
          -DEFI_FUNCTION_WRAPPER -mno-red-zone -mno-mmx -mno-sse \
          -fpic -fshort-wchar -fno-stack-protector \
          -Wall -Wextra -Wno-unused-parameter -std=c11 -O2 -nostdlib \
          -MMD -MP

LDFLAGS = -T $(GNUEFI_LDS) \
          -shared -Bsymbolic -L$(GNUEFI_LIB) \
          $(GNUEFI_CRT0) -lgnuefi -lefi

.PHONY: all clean distclean

all: $(BINDIR)/$(TARGET).efi

$(OBJDIR)/%.o: src/%.c | $(OBJDIR)
	$(CC) $(CFLAGS) -c $< -o $@

$(OBJDIR):
	mkdir -p $(OBJDIR)

$(BINDIR):
	mkdir -p $(BINDIR)

$(BINDIR)/$(TARGET).so: $(OBJS) | $(BINDIR)
	$(LD) $(LDFLAGS) $^ -o $@

$(BINDIR)/$(TARGET).efi: $(BINDIR)/$(TARGET).so
	$(OBJCOPY) -I elf64-x86-64 -O pei-x86-64 \
	           -j .text -j .sdata -j .data -j .dynamic -j .dynsym \
	           -j .rel -j .rela -j .reloc $^ $@
	@echo ""
	@echo "=== BUILD COMPLETE ==="
	@echo "Output: $@"
	@ls -lh $@

-include $(DEPS)

clean:
	rm -rf $(OBJDIR) $(BINDIR)

distclean: clean
	rm -rf deps
