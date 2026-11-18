package com.limelight.streamhub;

import java.io.IOException;
import java.util.concurrent.CancellationException;
import java.util.concurrent.atomic.AtomicInteger;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubSessionEndQueueTest {
    private final HubSessionEndQueue.Owner owner = new HubSessionEndQueue.Owner("computer-a", "client-a", "binding-a");
    private static final String LEASE = "lease-first-123";
    private static class Memory implements HubSessionEndQueue.Storage {
        String value = "";
        boolean writable = true;
        public String read() { return value; }
        public boolean write(String value) { if (!writable) return false; this.value = value; return true; }
    }
    @Test public void requestIsDurableBeforeSendAndCarriesOnlyExactCleanupIdentity() throws Exception {
        Memory memory = new Memory();
        HubSessionEndQueue queue = new HubSessionEndQueue(memory);
        queue.remember(owner, LEASE, true);
        AtomicInteger sends = new AtomicInteger();
        assertTrue(queue.retry(owner, true, body -> {
            assertFalse(memory.value.isEmpty());
            assertEquals(5, body.length());
            assertEquals("remote", body.getString("mode"));
            assertEquals(LEASE, body.getString("leaseId"));
            assertEquals("client-a", body.getString("clientId"));
            assertEquals("user", body.getString("exitReason"));
            assertTrue(body.getBoolean("endBackend"));
            sends.incrementAndGet();
        }, () -> { }));
        assertEquals(1, sends.get());
        assertEquals("", memory.value);
        assertFalse(queue.retry(owner, true, body -> fail("already acknowledged"), () -> { }));
    }
    @Test public void failedRequestSurvivesQueueRecreationForRetryBeforePreparing() throws Exception {
        Memory memory = new Memory();
        new HubSessionEndQueue(memory).remember(owner, LEASE, false);
        String saved = memory.value;
        try {
            new HubSessionEndQueue(memory).retry(owner, true, body -> { throw new IOException("lost reply"); }, () -> { });
            fail("lost reply accepted");
        } catch (IOException expected) { }
        assertEquals(saved, memory.value);
        assertTrue(new HubSessionEndQueue(memory).retry(owner, true,
                body -> assertEquals(LEASE, body.getString("leaseId")), () -> { }));
        assertEquals("", memory.value);
    }
    @Test public void differentComputerClientOrBindingNeverReceivesOldCleanup() throws Exception {
        for (HubSessionEndQueue.Owner changed : new HubSessionEndQueue.Owner[] {
                new HubSessionEndQueue.Owner("computer-b", "client-a", "binding-a"),
                new HubSessionEndQueue.Owner("computer-a", "client-b", "binding-a"),
                new HubSessionEndQueue.Owner("computer-a", "client-a", "binding-b") }) {
            Memory memory = new Memory();
            HubSessionEndQueue queue = new HubSessionEndQueue(memory);
            queue.remember(owner, LEASE, false);
            assertFalse(queue.retry(changed, true, body -> fail("old owner request"), () -> { }));
            assertEquals("", memory.value);
        }
    }
    @Test public void unsupportedHostCannotReceiveUnknownFieldOrGlobalCancelFallback() throws Exception {
        Memory memory = new Memory();
        HubSessionEndQueue queue = new HubSessionEndQueue(memory);
        queue.remember(owner, LEASE, false);
        try { queue.retry(owner, false, body -> fail("old host request"), () -> { }); fail(); }
        catch (IOException expected) { }
        assertFalse(memory.value.isEmpty());
    }
    @Test public void cancellationBeforeSendRetainsPendingLease() throws Exception {
        Memory memory = new Memory();
        HubSessionEndQueue queue = new HubSessionEndQueue(memory);
        queue.remember(owner, LEASE, false);
        try { queue.retry(owner, true, body -> fail("cancelled request"), () -> { throw new CancellationException(); }); fail(); }
        catch (CancellationException expected) { }
        assertFalse(memory.value.isEmpty());
    }
    @Test public void confirmedEndIsClearedEvenWhenUserCancelsImmediatelyAfterReply() throws Exception {
        Memory memory = new Memory();
        HubSessionEndQueue queue = new HubSessionEndQueue(memory);
        queue.remember(owner, LEASE, false);
        AtomicInteger checks = new AtomicInteger();
        try { queue.retry(owner, true, body -> { }, () -> { if (checks.incrementAndGet() == 2) throw new CancellationException(); }); fail(); }
        catch (CancellationException expected) { }
        assertEquals("", memory.value);
    }
    @Test public void failedPersistenceDoesNotPermitUnrecordedCleanup() throws Exception {
        Memory memory = new Memory(); memory.writable = false;
        try { new HubSessionEndQueue(memory).remember(owner, LEASE, false); fail(); }
        catch (IOException expected) { }
        assertEquals("", memory.value);
    }
    @Test public void newLeaseCannotOverwriteUnacknowledgedEnd() throws Exception {
        Memory memory = new Memory();
        HubSessionEndQueue queue = new HubSessionEndQueue(memory);
        queue.remember(owner, LEASE, true);
        String saved = memory.value;
        try { queue.remember(owner, "replacement-lease", false); fail(); }
        catch (IOException expected) { }
        assertEquals(saved, memory.value);
    }
    @Test public void corruptRecordNeverSendsCleanupWithGuessedOwnership() throws Exception {
        for (String damaged : new String[] {"not json", "{}", new JSONObject().put("leaseId", LEASE).toString()}) {
            Memory memory = new Memory(); memory.value = damaged;
            try { new HubSessionEndQueue(memory).retry(owner, true, body -> fail("guessed request"), () -> { }); fail(); }
            catch (IOException expected) { }
            assertEquals(damaged, memory.value);
        }
    }
    @Test public void capabilityRequiresExplicitNegotiation() throws Exception {
        assertFalse(HubSessionLease.supportsBackendRelease(new JSONObject()));
        assertFalse(HubSessionLease.supportsBackendRelease(new JSONObject().put("capabilities", new JSONObject())));
        assertTrue(HubSessionLease.supportsBackendRelease(new JSONObject().put("capabilities", new JSONObject().put("remoteBackendRelease", true))));
    }
    @Test public void completedReceiptMustMatchTheExactRequestedLeaseAndDevice() throws Exception {
        JSONObject request = new JSONObject().put("leaseId", LEASE).put("clientId", owner.client);
        JSONObject receipt = new JSONObject().put("leaseId", LEASE).put("clientId", owner.client)
                .put("completed", true).put("alreadyCompleted", true);
        HubSessionEndQueue.confirmReceipt(request, new JSONObject().put("backendRelease", receipt));
        for (JSONObject invalid : new JSONObject[] {
                new JSONObject(),
                new JSONObject(receipt.toString()).put("leaseId", "replacement-lease"),
                new JSONObject(receipt.toString()).put("clientId", "different-device"),
                new JSONObject(receipt.toString()).put("completed", false),
                new JSONObject(receipt.toString()).put("completed", "true") }) {
            try { HubSessionEndQueue.confirmReceipt(request, new JSONObject().put("backendRelease", invalid)); fail(); }
            catch (IOException expected) { }
        }
        try { HubSessionEndQueue.confirmReceipt(request, new JSONObject().put("success", true)); fail(); }
        catch (IOException expected) { }
    }
    @Test public void exactObsoleteReceiptRetiresOnlyTheOldRecordWithoutClaimingBackendIdle() throws Exception {
        Memory memory = new Memory();
        HubSessionEndQueue queue = new HubSessionEndQueue(memory);
        queue.remember(owner, LEASE, false);
        assertTrue(queue.retry(owner, true, request -> {
            JSONObject obsolete = new JSONObject().put("leaseId", LEASE).put("clientId", owner.client)
                    .put("completed", false).put("obsolete", true);
            HubSessionEndQueue.confirmReceipt(request, new JSONObject().put("backendRelease", obsolete));
        }, () -> { }));
        assertEquals("", memory.value);
    }
    @Test public void ambiguousOrMismatchedObsoleteReceiptCannotDiscardPendingCleanup() throws Exception {
        JSONObject exact = new JSONObject().put("leaseId", LEASE).put("clientId", owner.client)
                .put("completed", false).put("obsolete", true);
        for (JSONObject invalid : new JSONObject[] {
                new JSONObject(exact.toString()).put("leaseId", "replacement-lease"),
                new JSONObject(exact.toString()).put("clientId", "different-device"),
                new JSONObject(exact.toString()).put("completed", true),
                new JSONObject(exact.toString()).put("completed", "false"),
                new JSONObject(exact.toString()).put("obsolete", "true"),
                new JSONObject(exact.toString()).put("obsolete", false) }) {
            Memory memory = new Memory();
            HubSessionEndQueue queue = new HubSessionEndQueue(memory);
            queue.remember(owner, LEASE, false);
            String saved = memory.value;
            try {
                queue.retry(owner, true, request -> HubSessionEndQueue.confirmReceipt(request,
                        new JSONObject().put("backendRelease", invalid)), () -> { });
                fail();
            } catch (IOException expected) { }
            assertEquals(saved, memory.value);
        }
    }
}
