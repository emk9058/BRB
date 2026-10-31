<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import Loadout from './Loadout.vue'
import Party from './Party.vue'

interface ChampSlot {
  name: string
  position: string | null
  champion: string | null
  championId: number | null
  spell1: string | null
  spell2: string | null
  hovering: boolean
  acting: 'ban' | 'pick' | null
  banHoverId: number | null
  you: boolean
}

interface ChampOption {
  id: number
  name: string
}

interface Lobby {
  queueId: number
  queueName: string
  canInvite: boolean
  isLeader: boolean
  showRoles: boolean
  firstRole: string
  secondRole: string
  maxSize: number
  members: {
    summonerId: number
    name: string
    leader: boolean
    you: boolean
    firstRole: string
    secondRole: string
    icon: number
  }[]
  invites: { summonerId: number; name: string; state: string }[]
  canQueue?: boolean
}

interface ChampSelect {
  turn: string | null
  hoveredChampionId: number | null
  myTeam: ChampSlot[]
  theirTeam: ChampSlot[]
  champions: ChampOption[]
  myBans: (number | null)[]
  theirBans: (number | null)[]
}

const phase = ref<string | null>(null)
const title = ref<string | null>(null)
const detail = ref<string | null>(null)
const action = ref<string | null>(null)
const champSelect = ref<ChampSelect | null>(null)
const lobby = ref<Lobby | null>(null)
const search = ref('')
const busy = ref(false)
const panel = ref<'spells' | 'runes' | null>(null)
const spellSlot = ref(1)
const clockMs = ref<number | null>(null)
const clockDown = ref(true)
const clockPhase = ref<string | null>(null)
const clockTotalMs = ref<number | null>(null)
const readyResponse = ref<string | null>(null)
let clockBase = 0
let clockAt = 0
let timer: number | undefined
let clockTimer: number | undefined

const clockLabel = computed(() => {
  if (clockMs.value == null) return ''
  const seconds = Math.max(0, Math.ceil(clockMs.value / 1000))
  const minutes = Math.floor(seconds / 60)
  const rest = seconds % 60
  return `${minutes}:${rest.toString().padStart(2, '0')}`
})

const visibleChampions = computed(() => {
  const champs = champSelect.value?.champions ?? []
  const query = search.value.trim().toLowerCase()
  if (!query) return champs
  return champs.filter(champ => champ.name.toLowerCase().includes(query))
})

const hoveredName = computed(() => {
  const id = champSelect.value?.hoveredChampionId
  if (!id) return ''
  return champSelect.value?.champions.find(champ => champ.id === id)?.name ?? ''
})

const me = computed(() => champSelect.value?.myTeam.find(slot => slot.you) ?? null)

const readyFill = computed(() => {
  if (readyResponse.value === 'Accepted') return '100%'
  if (clockMs.value == null) return '100%'
  const total = clockTotalMs.value || 12000
  return `${Math.min(100, Math.max(0, (clockMs.value / total) * 100))}%`
})

const readyLocked = computed(() => readyResponse.value === 'Accepted' || readyResponse.value === 'Declined')

const waiting = computed(() => {
  if (phase.value === 'queue') return 'Searching for a match'
  if (phase.value === 'game') return 'The match is on your computer'
  if (phase.value === 'reconnect') return 'Reconnect in the League client'
  if (phase.value === 'closed') return 'Open League on this PC'
  if (phase.value === 'open') return 'Connected to League'
  return ''
})

async function refresh() {
  try {
    const response = await fetch('/api/state')
    const data = await response.json()
    phase.value = data.phase ?? null
    title.value = data.title
    detail.value = data.detail ?? null
    action.value = data.action ?? null
    champSelect.value = data.champSelect ?? null
    lobby.value = data.lobby ?? null
    syncClock(data.clockMs, data.clockDown, data.clockPhase)
    clockTotalMs.value = data.clockTotalMs ?? null
    readyResponse.value = data.readyResponse ?? null
    if (!champSelect.value) search.value = ''
  } catch {
    phase.value = null
    title.value = null
    detail.value = null
    action.value = null
    champSelect.value = null
    lobby.value = null
    syncClock(null, true, null)
    clockTotalMs.value = null
    readyResponse.value = null
  }
}

function syncClock(ms: number | null | undefined, down: boolean | undefined, label: string | null | undefined) {
  clockPhase.value = label || null
  if (ms == null) {
    clockMs.value = null
    return
  }
  clockDown.value = down !== false
  clockBase = ms
  clockAt = performance.now()
  clockMs.value = ms
}

function advanceClock() {
  if (clockMs.value == null) return
  const delta = performance.now() - clockAt
  clockMs.value = clockDown.value ? Math.max(0, clockBase - delta) : clockBase + delta
}

function openSpells(slot: number) {
  spellSlot.value = slot
  panel.value = 'spells'
}

function iconUrl(id: number) {
  return `https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/${id}.png`
}

function buttonLabel(current: string) {
  if (current === 'start') return 'Find match'
  if (current === 'accept') return 'Accept'
  return 'Cancel'
}

async function act() {
  if (!action.value || busy.value) return
  busy.value = true
  try {
    await fetch('/api/queue', { method: action.value === 'start' ? 'POST' : 'DELETE' })
    await refresh()
  } finally {
    busy.value = false
  }
}

async function hover(championId: number) {
  if (busy.value) return
  busy.value = true
  try {
    await fetch('/api/champ-select', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ championId, lock: false }),
    })
    await refresh()
  } finally {
    busy.value = false
  }
}

async function lockIn() {
  if (busy.value || !champSelect.value?.hoveredChampionId) return
  busy.value = true
  try {
    await fetch('/api/champ-select', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ championId: champSelect.value.hoveredChampionId, lock: true }),
    })
    await refresh()
  } finally {
    busy.value = false
  }
}

async function readyCheck(accept: boolean) {
  if (busy.value) return
  busy.value = true
  try {
    const path = accept ? '/api/ready-check' : '/api/ready-check/decline'
    await fetch(path, { method: 'POST' })
    await refresh()
  } finally {
    busy.value = false
  }
}

onMounted(() => {
  refresh()
  timer = window.setInterval(refresh, 1000)
  clockTimer = window.setInterval(advanceClock, 100)
})

onUnmounted(() => {
  window.clearInterval(timer)
  window.clearInterval(clockTimer)
})
</script>

<template>
  <main class="screen" :class="phase">
    <p v-if="title === null" class="status">PC app is not reachable.</p>
    <template v-else>
      <header class="top">
        <div>
          <h1>{{ title }}</h1>
          <p v-if="detail">{{ detail }}</p>
        </div>
        <div v-if="phase !== 'ready' && (clockPhase || clockLabel)" class="clock">
          <span v-if="clockPhase" class="clock-phase">{{ clockPhase }}</span>
          <span v-if="clockLabel">{{ clockLabel }}</span>
        </div>
      </header>

      <section v-if="phase === 'ready'" class="ready">
        <div class="ready-card" :class="{ accepted: readyResponse === 'Accepted', declined: readyResponse === 'Declined' }">
          <p class="ready-kicker">Queue</p>
          <h2>Match Found</h2>
          <p class="ready-time">{{ clockLabel || '0:12' }}</p>
          <div class="ready-track">
            <div class="ready-filament" :style="{ width: readyFill }"></div>
            <i class="ready-edge" :style="{ left: readyFill }"></i>
          </div>
          <button type="button" class="lcu confirm" :disabled="busy || readyLocked" @click="readyCheck(true)">
            {{ readyResponse === 'Accepted' ? 'Accepted' : 'Accept' }}
          </button>
          <button type="button" class="lcu deny" :disabled="busy || readyLocked" @click="readyCheck(false)">Decline</button>
        </div>
      </section>

      <section v-else-if="champSelect" class="draft">
        <div v-if="champSelect.myBans.length || champSelect.theirBans.length" class="ban-row">
          <div class="ban-side">
            <span v-for="(id, index) in champSelect.myBans" :key="'mb-' + index" class="ban">
              <img v-if="id" :src="iconUrl(id)" alt="" />
            </span>
          </div>
          <div class="ban-side enemy">
            <span v-for="(id, index) in champSelect.theirBans" :key="'tb-' + index" class="ban">
              <img v-if="id" :src="iconUrl(id)" alt="" />
            </span>
          </div>
        </div>
        <div class="draft-body">
        <div class="teams">
        <div class="team">
          <h2>Your Team</h2>
          <div v-for="(slot, index) in champSelect.myTeam" :key="'my-' + index" class="member">
            <div v-if="slot.spell1 || slot.spell2" class="spells">
              <img v-if="slot.spell1" :src="slot.spell1" alt="" />
              <img v-if="slot.spell2" :src="slot.spell2" alt="" />
            </div>
            <div class="face-slot" :class="{ acting: slot.acting, ban: slot.acting === 'ban', intent: slot.hovering }">
              <i v-if="slot.acting" class="ring"></i>
              <span class="disc"><img v-if="slot.championId" :src="iconUrl(slot.championId)" alt="" /></span>
            </div>
            <span v-if="slot.banHoverId" class="ban-hover" title="Ban">
              <img :src="iconUrl(slot.banHoverId)" alt="" />
            </span>
            <div class="who">
              <span class="idline">
                <span v-if="slot.name" class="player">{{ slot.name }}</span>
                <span v-if="slot.position" class="role">{{ slot.position }}</span>
              </span>
              <span class="pick">{{ slot.champion || (slot.you && champSelect.turn !== 'ban' ? hoveredName : '') }}</span>
            </div>
          </div>
        </div>

        <div class="team enemy">
          <h2>Enemy</h2>
          <div v-for="(slot, index) in champSelect.theirTeam" :key="'their-' + index" class="member">
            <div class="face-slot" :class="{ acting: slot.acting, ban: slot.acting === 'ban', intent: slot.hovering }">
              <i v-if="slot.acting" class="ring"></i>
              <span class="disc"><img v-if="slot.championId" :src="iconUrl(slot.championId)" alt="" /></span>
            </div>
            <div class="who">
              <span class="idline">
                <span v-if="slot.name" class="player">{{ slot.name }}</span>
                <span v-if="slot.position" class="role">{{ slot.position }}</span>
              </span>
              <span v-if="slot.champion" class="pick">{{ slot.champion }}</span>
            </div>
          </div>
        </div>
        </div>

        <div class="loadout-bar">
          <button type="button" class="spell-change" @click="openSpells(1)">
            <img v-if="me?.spell1" :src="me.spell1" alt="" />
            <span>Spell 1</span>
          </button>
          <button type="button" class="spell-change" @click="openSpells(2)">
            <img v-if="me?.spell2" :src="me.spell2" alt="" />
            <span>Spell 2</span>
          </button>
          <button type="button" class="runes-open" @click="panel = 'runes'">Runes</button>
        </div>

        <div class="picker">
          <template v-if="champSelect.champions.length">
            <div class="picker-head">
              <h2>{{ champSelect.turn === 'ban' ? 'Ban' : 'Pick your champion' }}</h2>
              <button v-if="champSelect.turn" type="button" class="lcu" :class="champSelect.turn === 'ban' ? 'deny' : 'confirm'" :disabled="busy || !champSelect.hoveredChampionId" @click="lockIn">
                {{ champSelect.turn === 'ban' ? 'Ban' : 'Lock in' }}
              </button>
            </div>
            <input v-model="search" type="search" placeholder="Search..." />
            <div class="champions">
              <button
                v-for="champ in visibleChampions"
                :key="champ.id"
                type="button"
                class="portrait"
                :class="{ hovered: champ.id === champSelect.hoveredChampionId }"
                :disabled="busy"
                @click="hover(champ.id)"
              >
                <img :src="iconUrl(champ.id)" :alt="champ.name" />
                <span>{{ champ.name }}</span>
              </button>
            </div>
          </template>
        </div>
        </div>
      </section>

      <Party v-else-if="phase === 'home' || phase === 'party'" :lobby="lobby" @changed="refresh" />

      <section v-else class="waiting">
        <p v-if="waiting">{{ waiting }}</p>
      </section>

      <footer v-if="action && action !== 'accept'" class="dock">
        <button type="button" class="lcu" :class="action === 'cancel' ? 'deny' : 'confirm'" :disabled="busy || (action === 'start' && lobby?.canQueue === false)" @click="act">
          {{ buttonLabel(action) }}
        </button>
      </footer>
    </template>
    <Loadout v-if="panel" :mode="panel" :slot="spellSlot" @close="panel = null" />
  </main>
</template>

<style scoped>
.screen {
  min-height: 100vh;
  min-height: 100dvh;
  display: flex;
  flex-direction: column;
  color: var(--ink);
  background: var(--bg) url("/magic-background.jpg") center / cover no-repeat;
  font-family: var(--body);
}

.screen.queue::before {
  content: "";
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.45);
}

.screen.champ {
  height: 100dvh;
  overflow: hidden;
  background: #010a13;
}

.screen.champ .top {
  padding: calc(0.4rem + env(safe-area-inset-top)) 0.6rem 0.35rem;
}

.screen.champ .top h1 {
  font-size: 1.05rem;
}

.screen.champ .top p {
  font-size: 0.8rem;
}

.screen.champ .top .clock {
  font-size: 1.25rem;
}

.status {
  margin: auto;
  font-size: 1.4rem;
}

.top {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.6rem;
  padding: calc(0.85rem + env(safe-area-inset-top)) 1rem 0.8rem;
  background: linear-gradient(180deg, rgba(1, 10, 19, 0.15), rgba(1, 10, 19, 0.72));
  border-bottom: 1px solid rgba(240, 230, 210, 0.42);
}

.top .clock {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  margin: 0;
  color: #f0e6d2;
  font-family: "LoL Display", "Times New Roman", serif;
  font-size: 1.7rem;
  line-height: 1;
  letter-spacing: 0.04em;
  font-variant-numeric: tabular-nums;
}

.clock-phase {
  margin-bottom: 0.2rem;
  color: #c8aa6e;
  font-size: 0.62rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.top h1,
.picker-head h2,
.team h2,
.ready-card h2 {
  margin: 0;
  font-family: "LoL Display", "Times New Roman", serif;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.top h1 {
  font-size: 1.65rem;
  color: var(--ink);
}

.top p {
  margin: 0.2rem 0 0;
  color: var(--muted);
  font-size: 1rem;
}

.waiting {
  flex: 1;
  display: grid;
  place-items: center;
  padding: 1.5rem;
  text-align: center;
}

.waiting p {
  margin: 0;
  color: var(--gold-soft);
  font-family: var(--display);
  font-size: 1.35rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.dock {
  position: relative;
  padding: 0.55rem 0.75rem calc(0.75rem + env(safe-area-inset-bottom));
  background: linear-gradient(0deg, rgba(1, 10, 19, 0.88), rgba(1, 10, 19, 0));
}

.lcu {
  position: relative;
  width: 100%;
  height: 4.2rem;
  margin: 0;
  border: 3px solid #c8aa6e;
  border-image: linear-gradient(to top, #785b28 0%, #c89c3c 55%, #c8aa6e 100%) 1;
  background: #1e2328;
  color: #cdbe91;
  font-family: "LoL Display", "Times New Roman", serif;
  font-size: 1.5rem;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  cursor: pointer;
}

.lcu.confirm {
  color: #a3c7c7;
  border-image: linear-gradient(to top, #0d404c 0%, #0596aa 50%, #0ac8b9 100%) 1;
}

.lcu.deny {
  color: #e8b0b8;
  border-image: linear-gradient(to top, #5c1a22 0%, #c6403b 50%, #ee241d 100%) 1;
}

.lcu:disabled {
  color: #5c5b57;
  border-image: none;
  border-color: #5c5b57;
  cursor: default;
}

.screen.ready {
  background:
    radial-gradient(ellipse at center, rgba(8, 20, 32, 0.2), rgba(1, 10, 19, 0.82)),
    var(--bg) url("/magic-background.jpg") center / cover no-repeat;
}

.screen.ready .top {
  background: transparent;
  border-bottom-color: transparent;
}

.ready {
  position: relative;
  flex: 1;
  display: grid;
  place-items: center;
  padding: 1rem;
}

.ready-card {
  width: min(92%, 26rem);
  padding: 1.35rem 1rem 1.15rem;
  text-align: center;
  background:
    linear-gradient(180deg, rgba(16, 28, 40, 0.92), rgba(6, 14, 24, 0.94));
  border: 2px solid transparent;
  border-image: linear-gradient(to top, #614a1f, #c8aa6e 45%, #f0e6d2) 1;
  box-shadow: 0 18px 50px rgba(0, 0, 0, 0.45), inset 0 0 40px rgba(10, 200, 185, 0.05);
}

.ready-kicker {
  margin: 0;
  color: var(--gold);
  font-family: var(--display);
  font-size: 0.72rem;
  letter-spacing: 0.22em;
  text-transform: uppercase;
}

.ready-card h2 {
  margin: 0.35rem 0 0;
  font-size: 2rem;
  color: var(--ink);
}

.ready-time {
  margin: 0.35rem 0 0;
  color: var(--gold-soft);
  font-family: var(--display);
  font-size: 1.35rem;
  letter-spacing: 0.08em;
  font-variant-numeric: tabular-nums;
}

.ready-track {
  position: relative;
  height: 0.45rem;
  margin: 1rem 0.35rem 1.25rem;
  border-radius: 999px;
  background: rgba(1, 10, 19, 0.8);
  box-shadow: inset 0 0 0 1px rgba(200, 170, 110, 0.55), 0 0 16px rgba(10, 200, 185, 0.12);
}

.ready-filament {
  position: relative;
  height: 100%;
  border-radius: inherit;
  background: linear-gradient(90deg, #0d3d4a, #0ac8b9 55%, #f3fff9 82%, #c8aa6e);
  box-shadow: 0 0 14px rgba(10, 200, 185, 0.85);
  overflow: hidden;
  transition: width 0.12s linear;
}

.ready-filament::after {
  content: "";
  position: absolute;
  inset: 0;
  background: linear-gradient(100deg, transparent 30%, rgba(255, 255, 255, 0.7), transparent 70%);
  transform: translateX(-120%);
  animation: filament 1.6s linear infinite;
}

.ready-edge {
  position: absolute;
  top: 50%;
  width: 0.72rem;
  height: 0.72rem;
  background: #f0e6d2;
  transform: translate(-50%, -50%) rotate(45deg);
  box-shadow: 0 0 10px #0ac8b9, 0 0 18px rgba(200, 170, 110, 0.9);
  transition: left 0.12s linear;
}

.ready-card.accepted .ready-filament,
.ready-card.accepted .ready-edge {
  transition-duration: 0.7s;
  transition-timing-function: cubic-bezier(0.16, 0.84, 0.32, 1);
}

.ready-card.accepted .ready-filament {
  background: linear-gradient(90deg, #785b28, #c8aa6e 40%, #fff6df);
  box-shadow: 0 0 16px rgba(200, 170, 110, 0.9);
}

.ready-card.declined .ready-filament {
  background: linear-gradient(90deg, #5c1a22, #ee241d 70%, #ffd0d0);
  box-shadow: 0 0 14px rgba(238, 36, 29, 0.75);
}

.ready-card .lcu {
  width: calc(100% - 0.4rem);
  margin: 0.45rem auto 0;
}

@keyframes filament {
  to { transform: translateX(220%); }
}

.draft {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.ban-row {
  display: flex;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.3rem 0.4rem 0.15rem;
}

.ban-side {
  display: flex;
  gap: 0.22rem;
  min-width: 0;
}

.ban-side.enemy {
  justify-content: flex-end;
}

.ban {
  width: 1.55rem;
  height: 1.55rem;
  border-radius: 50%;
  overflow: hidden;
  flex: 0 0 auto;
  background: #1e2328;
  box-shadow: inset 0 0 0 1px rgba(205, 190, 145, 0.5);
}

.ban img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.draft-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.teams {
  display: grid;
  grid-template-columns: 1fr 1fr;
  flex: 0 0 auto;
  overflow: visible;
}

.team {
  min-width: 0;
  overflow: visible;
}

.team + .team {
  border-left: 1px solid rgba(240, 230, 210, 0.18);
}

.team h2 {
  margin: 0.4rem 0.4rem 0.15rem;
  padding-bottom: 0.15rem;
  border-bottom: 1px solid rgba(200, 170, 110, 0.35);
  color: var(--gold);
  font-size: 0.68rem;
}

.member {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  margin: 0;
  padding: 0.28rem 0.35rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.14);
  border-left: 2px solid rgba(10, 200, 185, 0.72);
  background: linear-gradient(90deg, rgba(30, 35, 40, 0.92), rgba(1, 10, 19, 0.2));
}

.enemy .member {
  justify-content: flex-start;
  text-align: left;
  border-left: 0;
  border-right: 2px solid rgba(238, 36, 29, 0.78);
  background: linear-gradient(270deg, rgba(30, 35, 40, 0.92), rgba(1, 10, 19, 0.2));
}

.spells {
  display: flex;
  flex-direction: column;
  gap: 1px;
  flex: 0 0 auto;
}

.spells img {
  width: 1.2rem;
  height: 1.2rem;
  object-fit: cover;
  border: 1px solid #463714;
  background: #010a13;
}

.loadout-bar {
  display: flex;
  gap: 0.35rem;
  flex: 0 0 auto;
  padding: 0.35rem 0.4rem;
  border-top: 1px solid rgba(240, 230, 210, 0.25);
}

.spell-change,
.runes-open {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.3rem;
  min-width: 0;
  margin: 0;
  padding: 0.4rem 0.35rem;
  border: 2px solid #463714;
  background: rgba(1, 10, 19, 0.72);
  color: var(--gold-soft);
  font-family: var(--display);
  font-size: 0.68rem;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  cursor: pointer;
}

.spell-change:active,
.runes-open:active {
  border-color: var(--gold);
  color: var(--ink);
}

.spell-change img {
  width: 1.35rem;
  height: 1.35rem;
  object-fit: cover;
  border-radius: 50%;
}

.face-slot {
  position: relative;
  width: 2.95rem;
  height: 2.95rem;
  flex: 0 0 2.95rem;
  display: grid;
  place-items: center;
}

.ring {
  position: absolute;
  inset: 0;
  border-radius: 50%;
  background: conic-gradient(from 0deg, transparent 0 55%, #f0e6d2 82%, #c8aa6e 100%);
  mask: radial-gradient(farthest-side, transparent calc(100% - 2.5px), #000 calc(100% - 1.5px));
  animation: orbit 1.1s linear infinite;
  pointer-events: none;
}

.face-slot.ban .ring {
  background: conic-gradient(from 0deg, transparent 0 55%, #ffd0d0 82%, #ee241d 100%);
}

.disc {
  width: 2.4rem;
  height: 2.4rem;
  border-radius: 50%;
  overflow: hidden;
  position: relative;
  z-index: 1;
  background: #010a13;
  box-shadow: inset 0 0 0 1px rgba(205, 190, 145, 0.45);
}

.disc img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.face-slot.intent .disc {
  opacity: 0.55;
}

.ban-hover {
  position: relative;
  width: 1.35rem;
  height: 1.35rem;
  flex: 0 0 1.35rem;
  border-radius: 50%;
  overflow: hidden;
  background: #1e2328;
  box-shadow: inset 0 0 0 2px #ee241d;
}

.ban-hover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.ban-hover::after {
  content: "";
  position: absolute;
  z-index: 1;
  left: -15%;
  right: -15%;
  top: 50%;
  height: 2px;
  background: #ee241d;
  transform: rotate(-32deg);
  box-shadow: 0 0 0 1px #010a13;
}

@keyframes orbit {
  to { transform: rotate(360deg); }
}

.who {
  min-width: 0;
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-self: center;
}

.who strong,
.who span {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.who .idline {
  display: flex;
  align-items: baseline;
  gap: 0.35rem;
  min-width: 0;
}

.idline .player,
.idline .role {
  display: block;
}

.player {
  min-width: 0;
  flex: 1 1 auto;
  color: var(--ink);
  font-size: 0.72rem;
}

.role {
  flex: 0 0 auto;
  color: #c8aa6e;
  font-size: 0.68rem;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.pick {
  color: #f0e6d2;
  font-size: 0.72rem;
}

.picker {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  background: #010a13 url("/magic-background.jpg") center / cover no-repeat;
  border-top: 1px solid rgba(240, 230, 210, 0.35);
}

.picker-head {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.35rem 0.4rem 0;
}

.picker-head h2 {
  flex: 1;
  font-size: 0.85rem;
}

.picker-head .lcu {
  width: auto;
  min-width: 4.6rem;
  height: 2.1rem;
  font-size: 0.75rem;
}

.picker input {
  margin: 0.4rem 0.45rem 0;
  padding: 0.45rem 0.5rem;
  border: 2px solid #695625;
  background: rgba(1, 10, 19, 0.72);
  color: var(--ink);
  font: inherit;
  font-size: 0.9rem;
}

.champions {
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(4.4rem, 1fr));
  align-content: start;
  gap: 0.45rem;
  overflow: auto;
  padding: 0.45rem;
}

.portrait {
  width: auto;
  margin: 0;
  padding: 0;
  border: 2px solid transparent;
  background: transparent;
  color: #f0e6d2;
  cursor: pointer;
}

.portrait img {
  width: 100%;
  height: auto;
  aspect-ratio: 1;
  object-fit: cover;
  display: block;
  border-radius: 50%;
  overflow: hidden;
}

.portrait span {
  display: block;
  overflow: hidden;
  color: var(--muted);
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.62rem;
  line-height: 1.2;
  text-align: center;
}

.portrait.hovered {
  border-color: transparent;
}

.portrait.hovered span { color: var(--ink); }

.portrait.hovered img {
  box-shadow: 0 0 0 2px var(--gold), 0 0 12px rgba(200, 170, 110, 0.45);
}

.portrait:disabled {
  opacity: 0.6;
}
</style>
