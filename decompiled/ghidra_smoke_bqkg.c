/* smoke target: gpe.bqkg @ 0x0323BA54 (LAN discovery host listen) */

/* WARNING: Possible PIC construction at 0x058c65f4: Changing call to branch */
/* WARNING: Possible PIC construction at 0x058c691c: Changing call to branch */
/* WARNING: Removing unreachable block (ram,0x058c65f8) */
/* WARNING: Removing unreachable block (ram,0x058c6920) */
/* WARNING: Removing unreachable block (ram,0x058c66f0) */
/* WARNING: Removing unreachable block (ram,0x058c6708) */

undefined * gpe_bqkg(void)

{
  uint uVar1;
  undefined *puVar2;
  undefined1 *puVar3;
  int iVar4;
  uint uVar5;
  long lVar6;
  undefined8 *puVar7;
  long *plVar8;
  undefined8 *puVar9;
  undefined *puVar10;
  undefined *puVar11;
  undefined8 *puVar12;
  undefined8 uVar13;
  ulong uVar14;
  int *piVar15;
  long unaff_x19;
  long *plVar16;
  long unaff_x20;
  long *plVar17;
  undefined *puVar18;
  long *unaff_x22;
  long *plVar19;
  undefined8 *unaff_x23;
  undefined *unaff_x24;
  undefined8 *unaff_x25;
  undefined *puVar20;
  undefined1 auVar21 [16];
  undefined1 auVar22 [16];
  undefined1 auVar23 [12];
  undefined8 in_stack_00000000;
  undefined1 auStack_20 [8];
  undefined8 uStack_18;
  undefined8 uStack_10;
  
  func_0x058c9b18();
  func_0x058c90f8();
  lVar6 = *(long *)(unaff_x20 + 0x10);
  if (lVar6 == 0) {
    return (undefined *)0x0;
  }
  uStack_10 = in_stack_00000000;
  plVar17 = (long *)0x72b1000;
  if ((bRam00000000072b119a & 1) == 0) {
    func_0x02f9e53c(PTR_DAT_06e00048);
    func_0x02f9e53c(PTR_DAT_06e3ace0);
    func_0x02f9e53c(PTR_DAT_06e3ace8);
    func_0x02f9e53c(PTR_DAT_06e008e0);
    func_0x02f9e53c(PTR_DAT_06e3acf0);
    func_0x02f9e53c(PTR_DAT_06e3acf8);
    bRam00000000072b119a = 1;
  }
  plVar16 = *(long **)(lVar6 + 0x10);
  plVar8 = (long *)0x0;
  plVar19 = unaff_x22;
  puVar7 = unaff_x23;
  if (plVar16 != (long *)0x0) {
    lVar6 = *plVar16;
    uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
    if (uVar14 != 0) {
      piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
      do {
        if (*(long *)(piVar15 + -2) == *(long *)PTR_DAT_06e3ace0) {
          puVar7 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
          goto code_r0x058c601c;
        }
        uVar14 = uVar14 - 1;
        piVar15 = piVar15 + 4;
      } while (uVar14 != 0);
    }
    puVar7 = (undefined8 *)func_0x030009c4(plVar16,*(long *)PTR_DAT_06e3ace0,0);
code_r0x058c601c:
    plVar17 = (long *)PTR_DAT_06e00048;
    plVar8 = (long *)(*(code *)*puVar7)(plVar16,puVar7[1]);
    puVar7 = (undefined8 *)PTR_DAT_06e3ace8;
    plVar19 = (long *)PTR_DAT_06e008e0;
    if (plVar8 != (long *)0x0) {
      do {
        lVar6 = *plVar8;
        uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
        if (uVar14 != 0) {
          piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
          do {
            if (*(long *)(piVar15 + -2) == *plVar19) {
              puVar9 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
              goto code_r0x058c6094;
            }
            uVar14 = uVar14 - 1;
            piVar15 = piVar15 + 4;
          } while (uVar14 != 0);
        }
        puVar9 = (undefined8 *)func_0x030009c4(plVar8,*plVar19,0);
code_r0x058c6094:
        puVar10 = (undefined *)(*(code *)*puVar9)(plVar8,puVar9[1]);
        if (((ulong)puVar10 & 1) == 0) {
          unaff_x19 = 0;
          goto code_r0x058c611c;
        }
        lVar6 = *plVar8;
        uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
        if (uVar14 != 0) {
          piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
          do {
            if (*(long *)(piVar15 + -2) == *puVar7) {
              puVar9 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
              goto code_r0x058c60f0;
            }
            uVar14 = uVar14 - 1;
            piVar15 = piVar15 + 4;
          } while (uVar14 != 0);
        }
        puVar9 = (undefined8 *)func_0x030009c4(plVar8,*puVar7,0);
code_r0x058c60f0:
        auVar21 = (*(code *)*puVar9)(plVar8,puVar9[1]);
        if (auVar21._8_8_ == 0) goto code_r0x058c6190;
        func_0x058c57b4(auVar21._8_8_,auVar21._0_8_ & 0xffffffff);
      } while( true );
    }
    goto code_r0x058c6198;
  }
code_r0x058c6194:
  func_0x02f9e7d8();
  unaff_x22 = plVar19;
  unaff_x23 = puVar7;
code_r0x058c6198:
  puVar7 = unaff_x23;
  plVar19 = unaff_x22;
  func_0x02f9e7d8();
  while( true ) {
    auVar23 = func_0x02f9e7d0(unaff_x19);
    uStack_18 = auVar23._0_8_;
    if (auVar23._8_4_ != 1) break;
    plVar16 = (long *)func_0x0696b790();
    unaff_x19 = *plVar16;
    puVar10 = (undefined *)func_0x0696b7a0();
code_r0x058c611c:
    if (plVar8 != (long *)0x0) {
      lVar6 = *plVar8;
      uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
      if (uVar14 != 0) {
        piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
        do {
          if (*(long *)(piVar15 + -2) == *plVar17) {
            puVar9 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
            goto code_r0x058c616c;
          }
          uVar14 = uVar14 - 1;
          piVar15 = piVar15 + 4;
        } while (uVar14 != 0);
      }
      puVar9 = (undefined8 *)func_0x030009c4(plVar8,*plVar17,0);
code_r0x058c616c:
      puVar10 = (undefined *)(*(code *)*puVar9)(plVar8,puVar9[1]);
    }
    if (unaff_x19 == 0) {
      return puVar10;
    }
  }
  if (plVar8 != (long *)0x0) {
    lVar6 = *plVar8;
    uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
    if (uVar14 != 0) {
      piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
      do {
        if (*(long *)(piVar15 + -2) == *plVar17) {
          puVar9 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
          goto code_r0x058c622c;
        }
        uVar14 = uVar14 - 1;
        piVar15 = piVar15 + 4;
      } while (uVar14 != 0);
    }
    puVar9 = (undefined8 *)func_0x030009c4(plVar8,*plVar17,0);
code_r0x058c622c:
    (*(code *)*puVar9)(plVar8,puVar9[1]);
  }
  func_0x030a143c(uStack_18);
  func_0x02f9e7d0(0);
  puVar10 = &UNK_058c6250;
  auVar21 = func_0x02b4f5bc();
  puVar3 = auStack_20;
  do {
    puVar11 = auVar21._0_8_;
    *(undefined **)(puVar3 + -0x40) = puVar10;
    *(undefined8 **)(puVar3 + -0x38) = unaff_x25;
    *(undefined **)(puVar3 + -0x30) = unaff_x24;
    *(undefined8 **)(puVar3 + -0x28) = puVar7;
    *(long **)(puVar3 + -0x20) = plVar19;
    *(long **)(puVar3 + -0x18) = plVar17;
    *(undefined8 *)(puVar3 + -0x10) = 0;
    *(long **)(puVar3 + -8) = plVar8;
    puVar18 = (undefined *)0x72b1000;
    puVar10 = (undefined *)(auVar21._8_8_ & 0xffffffff);
    if ((bRam00000000072b1191 & 1) == 0) {
      func_0x02f9e53c(PTR_DAT_06e0b0e0);
      func_0x02f9e53c(PTR_DAT_06e3ac60);
      func_0x02f9e53c(PTR_DAT_06e3ac68);
      func_0x02f9e53c(PTR_DAT_06e3ac70);
      func_0x02f9e53c(PTR_DAT_06e3ac80);
      func_0x02f9e53c(PTR_DAT_06e3ac88);
      func_0x02f9e53c(PTR_DAT_06e3ac98);
      func_0x02f9e53c(PTR_DAT_06e3aca8);
      func_0x02f9e53c(PTR_DAT_06e3acb0);
      func_0x02f9e53c(PTR_DAT_06e3acb8);
      func_0x02f9e53c(PTR_DAT_06e3acc0);
      func_0x02f9e53c(PTR_DAT_06e3acc8);
      func_0x02f9e53c(PTR_DAT_06e3acd8);
      func_0x02f9e53c(PTR_DAT_06e3ad00);
      func_0x02f9e53c(PTR_DAT_06e3ad08);
      func_0x02f9e53c(PTR_DAT_06e3ad10);
      func_0x02f9e53c(PTR_DAT_06e3ad18);
      bRam00000000072b1191 = 1;
    }
    unaff_x24 = PTR_DAT_06e0b0e0;
    *(undefined8 *)(puVar3 + -0x60) = 0;
    *(undefined8 *)(puVar3 + -0x58) = 0;
    *(undefined8 *)(puVar3 + -0x50) = 0;
    *(undefined8 *)(puVar3 + -0x80) = 0;
    *(undefined8 *)(puVar3 + -0x78) = 0;
    *(undefined8 *)(puVar3 + -0x70) = 0;
    *(undefined8 *)(puVar3 + -0x98) = 0;
    *(undefined8 *)(puVar3 + -0x90) = 0;
    *(undefined8 *)(puVar3 + -0x88) = 0;
    if (*(long *)(puVar11 + 0x10) == 0) {
      puVar18 = (undefined *)0x0;
    }
    else {
      if (*(int *)(*(long *)PTR_DAT_06e0b0e0 + 0xe0) == 0) {
        func_0x02f9e6b4();
      }
      iVar4 = func_0x058a6de4(puVar10,0);
      unaff_x25 = (undefined8 *)PTR_DAT_06e3ac98;
      puVar20 = PTR_DAT_06e3ac60;
      lVar6 = *(long *)(puVar11 + 0x10);
      puVar9 = puVar7;
      if (lVar6 == 0) goto code_r0x058c662c;
      uVar5 = *(int *)(lVar6 + 0x18) * iVar4;
      func_0x041ab6b4(puVar3 + -0xb0,lVar6,*(undefined8 *)PTR_DAT_06e3acd8);
      *(undefined8 *)(puVar3 + -0x58) = *(undefined8 *)(puVar3 + -0xa8);
      *(undefined8 *)(puVar3 + -0x60) = *(undefined8 *)(puVar3 + -0xb0);
      *(undefined8 *)(puVar3 + -0x50) = *(undefined8 *)(puVar3 + -0xa0);
      while( true ) {
        puVar18 = (undefined *)(ulong)uVar5;
        uVar14 = func_0x054f1a60(puVar3 + -0x60,*unaff_x25);
        if ((uVar14 & 1) == 0) break;
        plVar19 = *(long **)(puVar3 + -0x50);
        if (*(int *)(*(long *)unaff_x24 + 0xe0) == 0) {
          func_0x02f9e6b4();
        }
        iVar4 = func_0x058a6634(plVar19,0);
        uVar5 = iVar4 + uVar5;
      }
      func_0x054f1a5c(puVar3 + -0x60,*(undefined8 *)puVar20);
      puVar7 = (undefined8 *)puVar20;
    }
    if (*(long *)(puVar11 + 0x18) != 0) {
      if (*(int *)(*(long *)unaff_x24 + 0xe0) == 0) {
        func_0x02f9e6b4();
      }
      uVar5 = func_0x058a6de4(puVar10,0);
      puVar9 = puVar7;
      if (*(long *)(puVar11 + 0x18) == 0) goto code_r0x058c662c;
      uVar1 = *(uint *)(*(long *)(puVar11 + 0x18) + 0x18);
      puVar7 = (undefined8 *)(ulong)uVar1;
      plVar19 = (long *)(ulong)uVar5;
      iVar4 = func_0x058a6854(1,0);
      puVar9 = puVar7;
      if (*(long *)(puVar11 + 0x18) == 0) goto code_r0x058c662c;
      puVar18 = (undefined *)
                (ulong)((int)puVar18 + uVar1 * uVar5 +
                       *(int *)(*(long *)(puVar11 + 0x18) + 0x18) * iVar4);
    }
    if (*(long *)(puVar11 + 0x20) != 0) {
      if (*(int *)(*(long *)unaff_x24 + 0xe0) == 0) {
        func_0x02f9e6b4();
      }
      uVar5 = func_0x058a6de4(puVar10,0);
      puVar9 = puVar7;
      if (*(long *)(puVar11 + 0x20) == 0) goto code_r0x058c662c;
      uVar1 = *(uint *)(*(long *)(puVar11 + 0x20) + 0x18);
      puVar7 = (undefined8 *)(ulong)uVar1;
      plVar19 = (long *)(ulong)uVar5;
      iVar4 = func_0x058a684c(1,0);
      puVar9 = puVar7;
      if (*(long *)(puVar11 + 0x20) == 0) goto code_r0x058c662c;
      puVar18 = (undefined *)
                (ulong)((int)puVar18 + uVar1 * uVar5 +
                       *(int *)(*(long *)(puVar11 + 0x20) + 0x18) * iVar4);
    }
    puVar12 = puVar7;
    if (*(long *)(puVar11 + 0x28) != 0) {
      if (*(int *)(*(long *)unaff_x24 + 0xe0) == 0) {
        func_0x02f9e6b4();
      }
      iVar4 = func_0x058a6de4(puVar10,0);
      unaff_x25 = (undefined8 *)PTR_DAT_06e3ac80;
      puVar12 = (undefined8 *)PTR_DAT_06e3ac70;
      lVar6 = *(long *)(puVar11 + 0x28);
      puVar9 = puVar7;
      if (lVar6 == 0) goto code_r0x058c662c;
      uVar5 = (int)puVar18 + *(int *)(lVar6 + 0x18) * iVar4;
      func_0x041027e8(puVar3 + -0xb0,lVar6,*(undefined8 *)PTR_DAT_06e3acc8);
      *(undefined8 *)(puVar3 + -0x78) = *(undefined8 *)(puVar3 + -0xa8);
      *(undefined8 *)(puVar3 + -0x80) = *(undefined8 *)(puVar3 + -0xb0);
      *(undefined8 *)(puVar3 + -0x70) = *(undefined8 *)(puVar3 + -0xa0);
      while( true ) {
        puVar18 = (undefined *)(ulong)uVar5;
        uVar14 = func_0x054e51ec(puVar3 + -0x80,*unaff_x25);
        if ((uVar14 & 1) == 0) break;
        plVar19 = *(long **)(puVar3 + -0x70);
        if (*(int *)(*(long *)unaff_x24 + 0xe0) == 0) {
          func_0x02f9e6b4();
        }
        iVar4 = func_0x058a6b1c(plVar19,0);
        uVar5 = iVar4 + uVar5;
      }
      func_0x054e51e8(puVar3 + -0x80,*puVar12);
    }
    do {
      if (*(long *)(puVar11 + 0x30) == 0) {
        return puVar18;
      }
      if (*(int *)(*(long *)unaff_x24 + 0xe0) == 0) {
        func_0x02f9e6b4();
      }
      iVar4 = func_0x058a6de4((ulong)puVar10 & 0xffffffff,0);
      puVar2 = PTR_DAT_06e3ac88;
      puVar20 = PTR_DAT_06e3ac68;
      lVar6 = *(long *)(puVar11 + 0x30);
      puVar9 = puVar12;
      if (lVar6 != 0) {
        puVar18 = (undefined *)(ulong)(uint)((int)puVar18 + iVar4 * *(int *)(lVar6 + 0x18) * 2);
        func_0x041027e8(puVar3 + -0x98,lVar6,*(undefined8 *)PTR_DAT_06e3acc0);
        uVar14 = func_0x054e51ec(puVar3 + -0x98,*(undefined8 *)puVar2);
        if ((uVar14 & 1) == 0) {
          func_0x054e51e8(puVar3 + -0x98,*(undefined8 *)puVar20);
          return puVar18;
        }
        lVar6 = *(long *)(puVar3 + -0x88);
        puVar11 = puVar2;
        puVar10 = puVar20;
        if (lVar6 != 0) {
          puVar20 = &UNK_058c65f8;
          puVar9 = unaff_x25;
          goto code_r0x058c6760;
        }
        func_0x02f9e7d8();
      }
code_r0x058c662c:
      auVar23 = func_0x02f9e7d8();
      plVar19 = auVar23._0_8_;
      if (auVar23._8_4_ != 1) {
        func_0x054e51e8(puVar3 + -0x80,*puVar9);
        goto code_r0x058c674c;
      }
      plVar17 = (long *)func_0x0696b790(plVar19);
      lVar6 = *plVar17;
      func_0x0696b7a0();
      func_0x054e51e8(puVar3 + -0x80,*puVar9);
      puVar12 = (undefined8 *)0x0;
      unaff_x25 = puVar9;
    } while (lVar6 == 0);
    func_0x02f9e7d0(lVar6);
    func_0x054f1a5c(puVar3 + -0x60,*puVar9);
code_r0x058c674c:
    func_0x030a143c(plVar19);
    puVar12 = (undefined8 *)0x0;
    func_0x02f9e7d0(0);
    puVar20 = &UNK_058c6760;
    lVar6 = func_0x02b4f5bc();
code_r0x058c6760:
    *(undefined **)(puVar3 + -0xe0) = puVar20;
    *(undefined8 **)(puVar3 + -0xd8) = puVar12;
    *(long **)(puVar3 + -0xd0) = plVar19;
    *(undefined **)(puVar3 + -200) = puVar18;
    *(undefined **)(puVar3 + -0xc0) = puVar10;
    *(undefined **)(puVar3 + -0xb8) = puVar11;
    if ((bRam00000000072b119b & 1) == 0) {
      func_0x02f9e53c(PTR_DAT_06e00048);
      func_0x02f9e53c(PTR_DAT_06e3ace0);
      func_0x02f9e53c(PTR_DAT_06e3ace8);
      func_0x02f9e53c(PTR_DAT_06e008e0);
      func_0x02f9e53c(PTR_DAT_06e3acf0);
      func_0x02f9e53c(PTR_DAT_06e3acf8);
      bRam00000000072b119b = 1;
    }
    plVar16 = *(long **)(lVar6 + 0x10);
    plVar8 = (long *)0x0;
    plVar17 = (long *)puVar18;
    puVar7 = puVar12;
    if (plVar16 == (long *)0x0) goto code_r0x058c69a4;
    lVar6 = *plVar16;
    uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
    if (uVar14 != 0) {
      piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
      do {
        if (*(long *)(piVar15 + -2) == *(long *)PTR_DAT_06e3ace0) {
          puVar7 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
          goto code_r0x058c6828;
        }
        uVar14 = uVar14 - 1;
        piVar15 = piVar15 + 4;
      } while (uVar14 != 0);
    }
    puVar7 = (undefined8 *)func_0x030009c4(plVar16,*(long *)PTR_DAT_06e3ace0,0);
code_r0x058c6828:
    plVar19 = (long *)PTR_DAT_06e00048;
    plVar8 = (long *)(*(code *)*puVar7)(plVar16,puVar7[1]);
    puVar7 = (undefined8 *)PTR_DAT_06e3ace8;
    plVar17 = (long *)PTR_DAT_06e008e0;
    if (plVar8 == (long *)0x0) goto code_r0x058c69a8;
    lVar6 = *plVar8;
    uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
    if (uVar14 != 0) {
      piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
      do {
        if (*(long *)(piVar15 + -2) == *(long *)PTR_DAT_06e008e0) {
          puVar12 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
          goto code_r0x058c68a4;
        }
        uVar14 = uVar14 - 1;
        piVar15 = piVar15 + 4;
      } while (uVar14 != 0);
    }
    puVar12 = (undefined8 *)func_0x030009c4(plVar8,*(long *)PTR_DAT_06e008e0,0);
code_r0x058c68a4:
    uVar14 = (*(code *)*puVar12)(plVar8,puVar12[1]);
    if ((uVar14 & 1) == 0) {
      puVar18 = (undefined *)0x0;
      goto code_r0x058c692c;
    }
    lVar6 = *plVar8;
    uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
    if (uVar14 != 0) {
      piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
      do {
        if (*(long *)(piVar15 + -2) == *puVar7) {
          puVar12 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
          goto code_r0x058c6900;
        }
        uVar14 = uVar14 - 1;
        piVar15 = piVar15 + 4;
      } while (uVar14 != 0);
    }
    puVar12 = (undefined8 *)func_0x030009c4(plVar8,*puVar7,0);
code_r0x058c6900:
    auVar22 = (*(code *)*puVar12)(plVar8,puVar12[1]);
    if (auVar22._8_8_ == 0) goto code_r0x058c69a0;
    auVar21._8_8_ = auVar22._0_8_ & 0xffffffff;
    auVar21._0_8_ = auVar22._8_8_;
    puVar10 = &UNK_058c6920;
    puVar3 = puVar3 + -0xe0;
    unaff_x25 = puVar9;
  } while( true );
code_r0x058c6190:
  func_0x02f9e7d8();
  goto code_r0x058c6194;
code_r0x058c69a0:
  func_0x02f9e7d8();
code_r0x058c69a4:
  func_0x02f9e7d8();
  puVar18 = (undefined *)plVar17;
  puVar12 = puVar7;
code_r0x058c69a8:
  puVar7 = puVar12;
  func_0x02f9e7d8();
  while( true ) {
    auVar23 = func_0x02f9e7d0(puVar18);
    if (auVar23._8_4_ != 1) break;
    puVar12 = (undefined8 *)func_0x0696b790();
    puVar18 = (undefined *)*puVar12;
    func_0x0696b7a0();
code_r0x058c692c:
    if (plVar8 != (long *)0x0) {
      lVar6 = *plVar8;
      uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
      if (uVar14 != 0) {
        piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
        do {
          if (*(long *)(piVar15 + -2) == *plVar19) {
            puVar12 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
            goto code_r0x058c697c;
          }
          uVar14 = uVar14 - 1;
          piVar15 = piVar15 + 4;
        } while (uVar14 != 0);
      }
      puVar12 = (undefined8 *)func_0x030009c4(plVar8,*plVar19,0);
code_r0x058c697c:
      (*(code *)*puVar12)(plVar8,puVar12[1]);
    }
    if (puVar18 == (undefined *)0x0) {
      return (undefined *)0x0;
    }
  }
  if (plVar8 != (long *)0x0) {
    lVar6 = *plVar8;
    uVar14 = (ulong)*(ushort *)(lVar6 + 0x12e);
    if (uVar14 != 0) {
      piVar15 = (int *)(*(long *)(lVar6 + 0xb0) + 8);
      do {
        if (*(long *)(piVar15 + -2) == *plVar19) {
          puVar12 = (undefined8 *)(lVar6 + (long)*piVar15 * 0x10 + 0x138);
          goto code_r0x058c6a40;
        }
        uVar14 = uVar14 - 1;
        piVar15 = piVar15 + 4;
      } while (uVar14 != 0);
    }
    puVar12 = (undefined8 *)func_0x030009c4(plVar8,*plVar19,0);
code_r0x058c6a40:
    (*(code *)*puVar12)(plVar8,puVar12[1]);
  }
  func_0x030a143c(auVar23._0_8_);
  func_0x02f9e7d0(0);
  auVar21 = func_0x02b4f5bc();
  lVar6 = auVar21._8_8_;
  puVar10 = auVar21._0_8_;
  *(undefined **)(puVar3 + -0x120) = &UNK_058c6a64;
  *(undefined8 **)(puVar3 + -0x118) = puVar9;
  *(undefined **)(puVar3 + -0x110) = unaff_x24;
  *(undefined8 **)(puVar3 + -0x108) = puVar7;
  *(long **)(puVar3 + -0x100) = plVar19;
  *(undefined8 *)(puVar3 + -0xf8) = 0;
  *(long *)(puVar3 + -0xf0) = auVar23._0_8_;
  *(long **)(puVar3 + -0xe8) = plVar8;
  if ((bRam00000000072b1192 & 1) == 0) {
    func_0x02f9e53c(PTR_DAT_06e3ad20);
    func_0x02f9e53c(PTR_DAT_06e3ad28);
    func_0x02f9e53c(PTR_DAT_06e3ad30);
    func_0x02f9e53c(PTR_DAT_06e3ad38);
    bRam00000000072b1192 = 1;
  }
  puVar2 = PTR_DAT_06e3ad38;
  puVar20 = PTR_DAT_06e3ad30;
  puVar18 = PTR_DAT_06e3ad28;
  puVar11 = PTR_DAT_06e3ad20;
  if (lVar6 != 0) {
    uVar13 = func_0x03c49660(*(undefined8 *)(puVar10 + 0x10),*(undefined8 *)(lVar6 + 0x10),
                             *(undefined8 *)PTR_DAT_06e3ad30);
    *(undefined8 *)(puVar10 + 0x10) = uVar13;
    func_0x02f9e4e0(puVar10 + 0x10,uVar13);
    puVar7 = (undefined8 *)(puVar10 + 0x18);
    uVar13 = func_0x03c49564(*puVar7,*(undefined8 *)(lVar6 + 0x18),*(undefined8 *)puVar18);
    *puVar7 = uVar13;
    func_0x02f9e4e0(puVar7,uVar13);
    puVar7 = (undefined8 *)(puVar10 + 0x20);
    uVar13 = func_0x03c49660(*puVar7,*(undefined8 *)(lVar6 + 0x20),*(undefined8 *)puVar20);
    *puVar7 = uVar13;
    func_0x02f9e4e0(puVar7,uVar13);
    puVar7 = (undefined8 *)(puVar10 + 0x28);
    uVar13 = func_0x03c49468(*puVar7,*(undefined8 *)(lVar6 + 0x28),*(undefined8 *)puVar11);
    *puVar7 = uVar13;
    func_0x02f9e4e0(puVar7,uVar13);
    puVar7 = (undefined8 *)(puVar10 + 0x30);
    uVar13 = func_0x03c49468(*puVar7,*(undefined8 *)(lVar6 + 0x30),*(undefined8 *)puVar2);
    *puVar7 = uVar13;
    func_0x02f9e4e0(puVar7,uVar13);
    return puVar10;
  }
  auVar21 = func_0x02f9e7d8();
  puVar11 = PTR_DAT_06e3ad40;
  *(undefined **)(puVar3 + -0x150) = &UNK_058c6bb4;
  *(long **)(puVar3 + -0x140) = plVar19;
  *(undefined8 *)(puVar3 + -0x138) = 0x72b1000;
  *(undefined8 *)(puVar3 + -0x130) = 0;
  *(undefined **)(puVar3 + -0x128) = puVar10;
  if ((bRam00000000072b1193 & 1) == 0) {
    func_0x02f9e53c(PTR_DAT_06e3ad40);
    bRam00000000072b1193 = 1;
  }
  puVar7 = (undefined8 *)(auVar21._0_8_ + 0x10);
  uVar13 = func_0x03c49274(*puVar7,auVar21._8_8_,*(undefined8 *)puVar11);
  *puVar7 = uVar13;
  func_0x02f9e4e0(puVar7,uVar13);
  return auVar21._0_8_;
}


