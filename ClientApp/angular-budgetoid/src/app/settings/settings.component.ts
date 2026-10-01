import { BreakpointObserver } from '@angular/cdk/layout';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  OnInit,
  ViewContainerRef,
  afterNextRender,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import type { DialogConfig } from '@angular/cdk/dialog';
import {
  MatBottomSheet,
  type MatBottomSheetConfig,
  type MatBottomSheetRef,
} from '@angular/material/bottom-sheet';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, type MatDialogRef } from '@angular/material/dialog';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { ProviderDepartureService } from '@app-core/services/provider-departure.service';
import { AccountUnlockService } from './account-unlock.service';
import { toCredentialRow, type CredentialRow } from './credential-row';
import {
  ERASE_DIALOG_CONSEQUENCE_ID,
  ERASE_DIALOG_DISMISS_ID,
  ERASE_DIALOG_FIELD_ID,
  ERASE_DIALOG_TITLE_ID,
  EraseDialogComponent,
} from './erase-dialog.component';
import {
  EmailChangeFlowService,
  type EmailChangeOuterHold,
} from './email-change-flow.service';
import { ErasureFlowService } from './erasure-flow.service';
import { KeyRotationSectionComponent } from './key-rotation-section.component';
import { RotationFlowService } from './rotation-flow.service';
import { SettingsService } from './settings.service';

// The shell's own split between the bottom bar and the rail
// (`shell.component.scss`), which is also where the erasure confirmation changes
// from a bottom sheet to a centred dialog.
const EXPANDED = '(min-width: 960px)';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  // **Key rotation is a component and not two hundred more lines of this
  // screen's template**, and the reason is in that file's own header: it is the
  // one section here with a gate in it, and a gate that is half attribute and
  // half handler wants a handler that is about one press.
  imports: [MatButtonModule, KeyRotationSectionComponent],
  // All three services' lifetime is this screen's. Provided here rather than at
  // the root so an export outcome cannot survive a navigation away and reappear
  // as a claim about a visit that has exported nothing — and so an *abandoned*
  // unlock attempt, or an abandoned press of Rotate, dies with the screen it was
  // started on.
  //
  // **`RotationFlowService` is provided here and not on the section**, which is
  // the same distinction the two above keep: the *flow* is an attempt, and an
  // attempt belongs to the screen. What the attempt drives is
  // `KeyRotationService`, which is root-provided because a run survives the tab
  // that began it as server state and has to be readable by the three content
  // screens while it is in flight.
  //
  // `AccountKeyCustodyService` is deliberately **not** in this list. What the
  // attempt produces is state of the **session**, which outlives every screen,
  // so custody is root-provided and read from there; route-providing it on
  // `app` is the near miss `account-keys.md` refuses, because `guestGuard`
  // bouncing an authenticated visitor off `/welcome` destroys that injector and
  // discards the keys with nothing on screen going red.
  //
  // **`ErasureFlowService` is the erasure dialog's attempt, provided here for
  // the same reason.** The dialog is opened with this screen's view container,
  // so its content resolves this instance; an attempt abandoned on the screen
  // dies with it.
  //
  // **`EmailChangeFlowService` holds the Google answer a return brought back**,
  // and it is provided here so that answer dies with the screen: nothing at the
  // root holds one, and the screen's teardown aborts a press that has not posted.
  providers: [
    SettingsService,
    AccountUnlockService,
    RotationFlowService,
    ErasureFlowService,
    EmailChangeFlowService,
  ],
  styleUrls: ['./settings.component.scss'],
  templateUrl: './settings.component.html',
})
export class SettingsComponent implements OnInit {
  // Exposed to the template rather than re-signalled here: the service already
  // owns every piece of state this screen renders, and a second copy would only
  // be able to drift from it.
  protected readonly settings = inject(SettingsService);

  // The Account keys section reads **two** collaborators by name, and the two
  // names are the design.
  //
  // `custody` is the *session's* state: whether this tab holds the account's
  // keys. `unlocking` is *this screen's* state: whether a ceremony is running
  // and how the last one was refused. Two objects, two lifetimes, two
  // questions.
  //
  // **Two shapes were rejected and both look tidier.** Re-exporting custody's
  // signals off the flow as pass-throughs would read as one object owning
  // lockedness, and the next person adds a local `unlocked` signal to it —
  // a second copy of a fact only custody can know. Folding both into one
  // `computed()` screen-state enum is that same second copy written down, kept
  // in step with two independent classes by hand: the moment custody publishes
  // a state the enum has no arm for, the section renders whichever arm happens
  // to be last. The template composes them instead, on the two rules written
  // out at the point they are implemented.
  protected readonly custody = inject(AccountKeyCustodyService);
  protected readonly unlocking = inject(AccountUnlockService);

  // `null` all the way through, never flattened to an empty array: "the answer
  // has not arrived" and "nothing is attached to this account" are different
  // facts and the template renders them as different sentences.
  //
  // Everything this reads is total over what a 200 can carry, and that is a
  // requirement of the position rather than a nicety: a throw in here is a
  // throw during change detection, which Angular caches on the signal and
  // rethrows on every later read, so the failure is the whole screen below this
  // list for the rest of the visit rather than one spoiled row. The shape of
  // the *body* is refused a layer earlier, at the API boundary, where a failure
  // still has a sentence waiting for it.
  protected readonly credentialRows = computed<readonly CredentialRow[] | null>(
    () => {
      const credentials = this.settings.credentials();

      return credentials === null ? null : credentials.map(toCredentialRow);
    },
  );

  // The email change's attempt. Read by name, like `unlocking`: every
  // predicate both controls bind is the flow's, and its handlers refuse what
  // its predicates refuse, so the template composes no gate of its own.
  protected readonly emailChange = inject(EmailChangeFlowService);
  private readonly departure = inject(ProviderDepartureService);

  // **The email change holds controls on this screen as well as being held by
  // them.** Which of its two terms holds Unlock and the erasure trigger off,
  // departing first because it ends the screen and every sentence on it.
  // Departing is read off its owner rather than as the flow's `leaving` phase:
  // that phase is a projection of the same reading, and a projection is one
  // more place for the two to part company.
  protected readonly emailHold = computed<EmailChangeOuterHold | null>(() => {
    if (this.departure.departing()) {
      return 'departing';
    }

    return this.emailChange.asking() ? 'asking' : null;
  });

  // **Unlock's one predicate**, read by its `disabled` and by `unlock()`. The
  // screen owns it because the email flow is provided here and
  // `AccountUnlockService` sees nothing of it. `aria-busy` stays on `working`
  // alone: the email change's terms are not work this control is doing.
  protected readonly unlockHeld = computed(
    () => this.unlocking.working() || this.emailHold() !== null,
  );

  // The erasure trigger's one predicate, read by its `disabled` and by
  // `openErasure()`.
  protected readonly erasureHeld = computed(() => this.emailHold() !== null);

  // Which of the two email-change controls is drawn — exactly one, always.
  // Confirm from the moment a return holds an answer until a word drops it;
  // Change at rest and on the way to Google. One owner, because the template
  // draws from it and the focus rule below watches it leave.
  protected readonly confirmDrawn = computed(() => {
    const phase = this.emailChange.phase();

    return phase === 'waiting' || phase === 'asserting' || phase === 'changing';
  });

  // Whether the screen has rendered once. The email change's lines wait for
  // it, for the erasure dialog's reason: a line present at the first paint is
  // announced unreliably, and `unconfirmed` arrives with the load. Protected
  // and not a `#` field only because the template reads it.
  protected readonly painted = signal(false);

  private readonly erasureFlow = inject(ErasureFlowService);
  private readonly dialog = inject(MatDialog);
  private readonly sheet = inject(MatBottomSheet);
  private readonly breakpoints = inject(BreakpointObserver);
  private readonly viewContainerRef = inject(ViewContainerRef);

  // The control that opens the erasure confirmation, and where focus goes back
  // to when it closes. `read: ElementRef`, because it sits on a `mat-button`,
  // whose component instance is what the query answers by default.
  private readonly erasureTrigger = viewChild.required<
    string,
    ElementRef<HTMLButtonElement>
  >('erasureTrigger', { read: ElementRef });

  // The two email-change controls, each present only while it is drawn.
  // `read: ElementRef` for the reason the erasure trigger gives.
  private readonly changeControl = viewChild<
    string,
    ElementRef<HTMLButtonElement>
  >('changeControl', { read: ElementRef });
  private readonly confirmControl = viewChild<
    string,
    ElementRef<HTMLButtonElement>
  >('confirmControl', { read: ElementRef });

  // The erasure confirmation while it is open, whichever host it is in.
  private erasure:
    | MatDialogRef<EraseDialogComponent>
    | MatBottomSheetRef<EraseDialogComponent>
    | null = null;

  constructor() {
    // **The overlay lives no longer than this screen.** A router navigation
    // alone does not close a Material overlay, and every navigation off the
    // screen passes through this teardown — the tab going to Welcome after a
    // `204`, the interceptor sending an ended session there — so this is where
    // it is closed, through the ref's own close. The flow, provided here, dies
    // with the screen too, and abandons a press that has not posted as it goes.
    //
    // **The browser's Back closes the overlay first.** The CDK disposes it on
    // `popstate` whatever `disableClose` says — measured — before the screen
    // goes, which is why `openErasure` abandons a press when its overlay closes
    // mid-ceremony, not only here. Abandoning covers what has not been sent: an
    // erasing request already out still ends the session on a `204`.
    //
    // **Not the only thing that would, today, and it is kept on purpose.**
    // Because the overlay is opened with this screen's view container, the CDK
    // creates its container there too, and detaches the overlay when that
    // container is destroyed — measured: with this line removed, the Settings
    // spec's teardown case stays green. That is a consequence of where the CDK
    // happens to put the container, not a documented promise, and the design
    // book gives the closing to this screen; this line is what keeps the rule
    // true if the opener or the CDK changes.
    inject(DestroyRef).onDestroy(() => this.closeErasure());

    // **A return moves focus once, after the first render.** Into the waiting
    // state, straight to Confirm: the load is the second half of a press the
    // person made, and the next thing it needs is that one. Ending on
    // `unconfirmed`, to Change, the press its sentence offers. A load that
    // brought no return moves nothing.
    afterNextRender(() => {
      this.painted.set(true);

      if (this.confirmDrawn()) {
        this.confirmControl()?.nativeElement.focus();
      } else if (this.emailChange.word() === 'unconfirmed') {
        this.changeControl()?.nativeElement.focus();
      }
    });

    // **A word that ends the waiting state moves focus to Change**, because
    // Confirm, where focus stood, has left the DOM. Watched as Confirm leaving
    // rather than as a list of words, so a word the flow grows lands on the
    // right side by what it does to the section. A word that keeps the waiting
    // state leaves Confirm drawn and moves nothing — focus stays wherever the
    // person put it.
    //
    // A render effect and not an `effect()`: it touches the DOM, so it runs
    // once the pass that drew Change is done, and Change is there to take
    // focus.
    let confirmWasDrawn: boolean | null = null;

    afterRenderEffect(() => {
      const drawn = this.confirmDrawn();

      if (confirmWasDrawn === true && !drawn) {
        this.changeControl()?.nativeElement.focus();
      }

      confirmWasDrawn = drawn;
    });
  }

  /**
   * Runs an unlock, unless the control is held.
   *
   * The gate is here as well as in the attribute: Material's click-halt is
   * applied to anchors only, so on a `<button>` the press arrives whatever the
   * attribute says. `AccountUnlockService.unlock()` still guards on `working`
   * at its own entry, and this reads the wider predicate the screen draws.
   */
  protected unlock(): void {
    if (this.unlockHeld()) {
      return;
    }

    this.unlocking.unlock();
  }

  /**
   * Opens the erasure confirmation, and asks the server for nothing.
   *
   * **The host is the shell's split, chosen once, here.** Compact is a bottom
   * sheet, expanded a centred dialog, at the 960px the shell's bar and rail
   * change at. A resize while it is open does not swap hosts: swapping destroys
   * the field, the region and a ceremony in flight, and a person rotating a
   * tablet mid-prompt is the ordinary way to reach one.
   *
   * **With this screen's view container**, so the content resolves the
   * `ErasureFlowService` provided above rather than none — and so every open is
   * a new content instance with an empty field, over a flow reset to rest.
   *
   * No challenge is minted here: that waits for the commit. A nonce spent by
   * opening a dialog somebody then cancels is a live re-authentication
   * challenge nobody asked for.
   */
  protected openErasure(): void {
    // Held first: the trigger's `disabled` reads the same predicate, and the
    // press arrives on a `<button>` whatever that attribute says. Ungated, it
    // opens the dialog under a page about to leave or a passkey check already
    // running.
    if (this.erasureHeld() || this.erasure !== null) {
      return;
    }

    // **Every open is a fresh attempt**, so the last dialog's refusal does not
    // carry into this one — except `undetermined`, which `reset` keeps for the
    // screen's life, so this dialog opens withdrawn and asks for nothing.
    // `ErasureFlowService.reset` argues both halves, and refuses while a press
    // is running. Before the open, so the content's first pass already reads
    // the flow at rest.
    this.erasureFlow.reset();

    const viewContainerRef = this.viewContainerRef;
    // **One focus move, made by the host, to a control named by id**: the field
    // — the word is the next thing asked for — or, in a dialog opened
    // withdrawn, the dismiss, the one control left. A selector rather than
    // `first-tabbable`, which reaches the field only because of the order of
    // the markup. `erase-dialog.component.ts` owns the ids.
    const withdrawn = this.erasureFlow.failure() === 'undetermined';
    const autoFocus = `#${withdrawn ? ERASE_DIALOG_DISMISS_ID : ERASE_DIALOG_FIELD_ID}`;
    // **Back to the trigger, named rather than remembered.** The host's default
    // restores whatever had focus when it opened, and a tap on a phone — or a
    // click in a browser that does not focus buttons — leaves that as the body,
    // so a keyboard or screen-reader user would start again from the top.
    const restoreFocus = this.erasureTrigger().nativeElement;
    // Named by the title and described by the consequence, under both hosts.
    const ariaLabelledBy = ERASE_DIALOG_TITLE_ID;
    const ariaDescribedBy = ERASE_DIALOG_CONSEQUENCE_ID;

    let opened:
      | MatDialogRef<EraseDialogComponent>
      | MatBottomSheetRef<EraseDialogComponent>;

    if (this.breakpoints.isMatched(EXPANDED)) {
      opened = this.dialog.open(EraseDialogComponent, {
        viewContainerRef,
        autoFocus,
        restoreFocus,
        ariaLabelledBy,
        ariaDescribedBy,
        // The Dialogs and sheets chapter's maximum; the width below it is the
        // content's.
        maxWidth: '560px',
        width: '100%',
      });
    } else {
      // **`MatBottomSheetConfig` declares neither aria member, and both still
      // arrive.** `MatBottomSheet.open` spreads its config into the CDK dialog
      // it opens, and the sheet's container inherits the CDK container's host
      // bindings — the same two `MatDialog`'s container binds. Typed as the
      // union of the two configs so the passthrough is stated rather than
      // smuggled; `settings.component.spec.ts` reads both attributes off the
      // sheet, so a Material release that stopped passing them reddens there.
      // Set by hand instead, they are removed again by that same binding on the
      // container's first pass.
      const sheetConfig: MatBottomSheetConfig &
        Pick<DialogConfig, 'ariaLabelledBy' | 'ariaDescribedBy'> = {
        viewContainerRef,
        autoFocus,
        restoreFocus,
        ariaLabelledBy,
        ariaDescribedBy,
      };

      opened = this.sheet.open(EraseDialogComponent, sheetConfig);
    }

    this.erasure = opened;

    const closed =
      'afterClosed' in opened ? opened.afterClosed() : opened.afterDismissed();

    closed.subscribe(() => {
      if (this.erasure === opened) {
        this.erasure = null;
      }

      // **Closed from outside while the device is still being asked** — the
      // browser's Back, which the CDK honours whatever `disableClose` says. The
      // screen stays, the dialog does not, and a passkey answering now would
      // erase the account with nothing on screen to say so. Once the erasing
      // request is out there is nothing to abandon.
      if (this.erasureFlow.phase() === 'asserting') {
        this.erasureFlow.abandon();
      }
    });
  }

  private closeErasure(): void {
    const open = this.erasure;

    this.erasure = null;

    if (open === null) {
      return;
    }

    if ('close' in open) {
      open.close();
    } else {
      open.dismiss();
    }
  }

  public ngOnInit(): void {
    // The only work the screen starts on its own. The export is never begun
    // here — it writes a file to the user's disk, so it waits for the click.
    void this.settings.loadEmail();
    this.settings.loadCredentials();
    this.settings.loadRecoveryCodes();
  }
}
