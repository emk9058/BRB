<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import Loadout from './Loadout.vue'

interface GameMode {
  id: number
  name: string
  roles: boolean
}

interface LobbyMember {
  summonerId: number
  name: string
  leader: boolean
  you: boolean
  firstRole: string
  secondRole: string
  icon: number
}

interface LobbyInvite {
  summonerId: number
  name: string
  state: string
}

interface SwiftSlot {
  championId: number
  champion: string
  position: string
  spell1Icon: string | null
  spell2Icon: string | null
  primaryStyleId: number
  subStyleId: number
  perks: number[]
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
  members: LobbyMember[]
  invites: LobbyInvite[]
  swiftplay?: boolean
  slots?: SwiftSlot[]
  canQueue?: boolean
  errors?: string[]
  warnings?: string[]
}

interface Friend {
  summonerId: number
  name: string
  status: string
  icon: number
}

const props = defineProps<{ lobby: Lobby | null }>()
const emit = defineEmits<{ changed: [] }>()

const roles = [
  { id: 'TOP', name: 'Top' },
  { id: 'JUNGLE', name: 'Jungle' },
  { id: 'MIDDLE', name: 'Mid' },
  { id: 'BOTTOM', name: 'Bot' },
  { id: 'UTILITY', name: 'Support' },
  { id: 'FILL', name: 'Fill' },
]

const modes = ref<GameMode[]>([])
const leagueDown = ref(false)
let modesTimer: number | undefined
const friends = ref<Friend[]>([])
const query = ref('')
const error = ref('')
const busy = ref(false)
const inviting = ref(false)
const changing = ref(false)
const picking = ref<'first' | 'second' | null>(null)
const champFor = ref<number | null>(null)
const champQuery = ref('')
const champs = ref<{ id: number; name: string }[]>([])
const swiftRole = ref<number | null>(null)
const swiftEdit = ref<'spells' | 'runes' | null>(null)
const swiftIndex = ref<number | null>(null)
const swiftSpell = ref(1)

const activeSwift = computed(() =>
  swiftIndex.value == null ? null : props.lobby?.slots?.[swiftIndex.value] ?? null)

const visibleChamps = computed(() => {
  const text = champQuery.value.trim().toLowerCase()
  if (!text) return champs.value
  return champs.value.filter(champ => champ.name.toLowerCase().includes(text))
})

const roleName = (id: string | null) => roles.find(role => role.id === id)?.name ?? 'None'

function iconUrl(id: number) {
  return `https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/${id}.png`
}

function profileIcon(id: number) {
  return `https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/profile-icons/${id || 29}.jpg`
}

function inviteLabel(state: string) {
  if (state === 'Pending') return 'Invited'
  if (state === 'Declined') return 'Declined'
  if (state === 'Kicked') return 'Removed'
  return state
}

function roleText(first: string, second: string) {
  const primary = roleName(first)
  const secondary = roleName(second)
  return secondary === 'None' ? primary : `${primary} / ${secondary}`
}

const visibleFriends = () => {
  const text = query.value.trim().toLowerCase()
  if (!text) return friends.value
  return friends.value.filter(friend => friend.name.toLowerCase().includes(text))
}

async function loadModes() {
  try {
    const response = await fetch('/api/modes')
    if (!response.ok) {
      modes.value = []
      leagueDown.value = true
      return
    }
    leagueDown.value = false
    modes.value = await response.json()
  } catch {
    modes.value = []
    leagueDown.value = true
  }
}

async function send(path: string, method: string, body: unknown, message: string) {
  if (busy.value) return false
  busy.value = true
  error.value = ''
  try {
    const response = await fetch(path, {
      method,
      headers: body == null ? undefined : { 'Content-Type': 'application/json' },
      body: body == null ? undefined : JSON.stringify(body),
    })
    if (!response.ok) {
      error.value = message
      return false
    }
    emit('changed')
    return true
  } catch {
    error.value = 'The PC app is not reachable.'
    return false
  } finally {
    busy.value = false
  }
}

async function create(queueId: number) {
  if (await send('/api/lobby', 'POST', { queueId }, 'That party could not be created.'))
    changing.value = false
}

async function changeMode(queueId: number) {
  if (queueId === props.lobby?.queueId) {
    changing.value = false
    return
  }
  if (await send('/api/lobby', 'PUT', { queueId }, 'That mode could not be selected.'))
    changing.value = false
}

async function leave() {
  if (!confirm('Leave the party?')) return
  await send('/api/lobby', 'DELETE', null, 'The party could not be left.')
}

async function kick(member: LobbyMember) {
  if (!confirm(`Kick ${member.name}?`)) return
  await send('/api/lobby/kick', 'POST', { summonerId: member.summonerId }, 'That player could not be kicked.')
}

async function invite(summonerId: number | null, name: string | null) {
  const ok = await send('/api/lobby/invite', 'POST', { summonerId, name }, 'That player could not be invited.')
  if (ok && name)
    query.value = ''
}

function chooseRole(role: string) {
  if (!props.lobby) return
  let first = props.lobby.firstRole || 'UNSELECTED'
  let second = props.lobby.secondRole || 'UNSELECTED'
  if (role === 'FILL') {
    first = 'FILL'
    second = 'UNSELECTED'
  } else if (picking.value === 'first') {
    if (role === second) {
      second = first
      first = role
    } else {
      first = role
      if (role === 'FILL') second = 'UNSELECTED'
    }
  } else if (role === first) {
    second = 'UNSELECTED'
  } else {
    second = role
  }
  picking.value = null
  send('/api/lobby/roles', 'PUT', { first, second }, 'That role could not be selected.')
}

async function openInvite() {
  inviting.value = true
  error.value = ''
  try {
    const response = await fetch('/api/friends')
    friends.value = response.ok ? await response.json() : []
    if (!response.ok) error.value = 'Friends could not be loaded.'
  } catch {
    friends.value = []
    error.value = 'The PC app is not reachable.'
  }
}

async function openChamp(index: number) {
  champFor.value = index
  champQuery.value = ''
  error.value = ''
  try {
    const response = await fetch('/api/swift/champions')
    champs.value = response.ok ? await response.json() : []
    if (!response.ok) error.value = 'Champions could not be loaded.'
  } catch {
    champs.value = []
    error.value = 'The PC app is not reachable.'
  }
}

async function pickChamp(id: number) {
  const index = champFor.value
  if (index == null) return
  if (await send('/api/lobby/swift', 'PUT', { index, championId: id }, 'That champion could not be selected.'))
    champFor.value = null
}

function chooseSwiftRole(role: string) {
  const index = swiftRole.value
  swiftRole.value = null
  if (index == null) return
  send('/api/lobby/swift', 'PUT', { index, position: role }, 'That role could not be selected.')
}

function openSwift(index: number, mode: 'spells' | 'runes', spell = 1) {
  swiftIndex.value = index
  swiftSpell.value = spell
  swiftEdit.value = mode
}

onMounted(() => {
  loadModes()
  modesTimer = window.setInterval(loadModes, 2000)
})

onUnmounted(() => window.clearInterval(modesTimer))
</script>

<template>
  <section class="party">
    <template v-if="lobby">
      <p v-if="lobby.maxSize" class="count">{{ lobby.members.length }} of {{ lobby.maxSize }}</p>
      <p v-for="notice in lobby.errors ?? []" :key="notice" class="lobby-error">{{ notice }}</p>
      <p v-for="notice in lobby.warnings ?? []" :key="notice" class="lobby-warn">{{ notice }}</p>
      <div v-if="lobby.swiftplay" class="swift">
        <article v-for="(slot, index) in lobby.slots ?? []" :key="index" class="swift-card">
          <h3>Pick {{ index + 1 }}</h3>
          <button type="button" class="swift-champ" :disabled="busy" @click="openChamp(index)">
            <img v-if="slot.championId" :src="iconUrl(slot.championId)" alt="" />
            <span>{{ slot.champion || 'Champion' }}</span>
          </button>
          <button type="button" class="swift-role" :disabled="busy" @click="swiftRole = index">{{ roleName(slot.position) }}</button>
          <button type="button" class="swift-spell" :disabled="busy" @click="openSwift(index, 'spells', 1)">
            <img v-if="slot.spell1Icon" :src="slot.spell1Icon" alt="" />
          </button>
          <button type="button" class="swift-spell" :disabled="busy" @click="openSwift(index, 'spells', 2)">
            <img v-if="slot.spell2Icon" :src="slot.spell2Icon" alt="" />
          </button>
          <button type="button" class="swift-role" :disabled="busy" @click="openSwift(index, 'runes')">Runes</button>
        </article>
      </div>
      <div v-for="member in lobby.members" :key="member.summonerId" class="member" :class="{ you: member.you }">
        <img class="crest" :src="profileIcon(member.icon)" alt="" />
        <div class="who">
          <strong>{{ member.name }}</strong>
          <span v-if="member.leader || lobby.showRoles">
            <template v-if="member.leader">Leader</template>
            <template v-if="member.leader && lobby.showRoles"> · </template>
            <template v-if="lobby.showRoles">{{ roleText(member.firstRole, member.secondRole) }}</template>
          </span>
        </div>
        <div v-if="member.you && lobby.showRoles" class="role-picks">
          <button type="button" :disabled="busy" @click="picking = 'first'">{{ roleName(lobby.firstRole) }}</button>
          <button v-if="lobby.firstRole !== 'FILL'" type="button" :disabled="busy" @click="picking = 'second'">{{ roleName(lobby.secondRole) }}</button>
        </div>
        <button v-else-if="lobby.isLeader && !member.you" type="button" class="kick" :disabled="busy" @click="kick(member)">Kick</button>
      </div>
      <p v-for="invite in lobby.invites" :key="invite.summonerId + invite.state" class="invite">
        {{ invite.name }} · {{ inviteLabel(invite.state) }}
      </p>
      <button v-if="lobby.canInvite" type="button" class="invite-prompt" :disabled="busy" @click="openInvite">+ Invite others</button>
      <div class="actions">
        <button v-if="lobby.isLeader" type="button" class="lcu" :disabled="busy" @click="changing = true">Change mode</button>
        <button type="button" class="lcu deny" :disabled="busy" @click="leave">Leave</button>
      </div>
    </template>

    <template v-else>
      <h2>Summoner's Rift</h2>
      <p class="lead">Choose a mode</p>
      <p v-if="leagueDown" class="empty">Open League on this PC. Modes show up once the client is running.</p>
      <p v-else-if="!modes.length" class="empty">No Summoner's Rift modes are open.</p>
      <button v-for="mode in modes" :key="mode.id" type="button" class="queue" :disabled="busy" @click="create(mode.id)">
        <i class="diamond"></i>
        <span>{{ mode.name }}</span>
      </button>
    </template>

    <p v-if="error && !inviting && !changing && !picking && champFor === null && swiftRole === null" class="error">{{ error }}</p>

    <div v-if="changing" class="sheet">
      <header>
        <h2>Summoner's Rift</h2>
        <button type="button" class="text" @click="changing = false">Close</button>
      </header>
      <p v-if="error" class="error">{{ error }}</p>
      <button
        v-for="mode in modes"
        :key="mode.id"
        type="button"
        class="queue"
        :class="{ on: mode.id === lobby?.queueId }"
        :disabled="busy"
        @click="lobby ? changeMode(mode.id) : create(mode.id)"
      >
        <i class="diamond"></i>
        <span>{{ mode.name }}</span>
      </button>
    </div>

    <div v-if="picking" class="sheet">
      <header>
        <h2>{{ picking === 'first' ? 'Primary role' : 'Secondary role' }}</h2>
        <button type="button" class="text" @click="picking = null">Close</button>
      </header>
      <div class="role-grid">
        <button v-for="role in roles" :key="role.id" type="button" class="role-tile" :disabled="busy" @click="chooseRole(role.id)">
          <i class="diamond"></i>
          {{ role.name }}
        </button>
        <button v-if="picking === 'second'" type="button" class="role-tile" :disabled="busy" @click="chooseRole('UNSELECTED')">
          <i class="diamond"></i>
          None
        </button>
      </div>
    </div>

    <div v-if="champFor !== null" class="sheet">
      <header>
        <h2>Champion</h2>
        <button type="button" class="text" @click="champFor = null">Close</button>
      </header>
      <p v-if="error" class="error">{{ error }}</p>
      <input v-model="champQuery" class="champ-search" type="search" placeholder="Search..." />
      <div class="champ-grid">
        <button v-for="champ in visibleChamps" :key="champ.id" type="button" class="portrait" :disabled="busy" @click="pickChamp(champ.id)">
          <img :src="iconUrl(champ.id)" :alt="champ.name" />
          <span>{{ champ.name }}</span>
        </button>
      </div>
    </div>

    <div v-if="swiftRole !== null" class="sheet">
      <header>
        <h2>Role</h2>
        <button type="button" class="text" @click="swiftRole = null">Close</button>
      </header>
      <div class="role-grid">
        <button v-for="role in roles.filter(role => role.id !== 'FILL')" :key="role.id" type="button" class="role-tile" :disabled="busy" @click="chooseSwiftRole(role.id)">
          <i class="diamond"></i>
          {{ role.name }}
        </button>
      </div>
    </div>

    <Loadout
      v-if="swiftEdit && activeSwift && swiftIndex !== null"
      :mode="swiftEdit"
      :slot="swiftSpell"
      :swift-index="swiftIndex"
      :swift-perks="activeSwift.perks"
      :swift-primary="activeSwift.primaryStyleId"
      :swift-sub="activeSwift.subStyleId"
      @close="swiftEdit = null"
      @changed="emit('changed')"
    />

    <div v-if="inviting" class="sheet">
      <header>
        <h2>Invite</h2>
        <button type="button" class="text" @click="inviting = false">Close</button>
      </header>
      <p v-if="error" class="error">{{ error }}</p>
      <form class="search" @submit.prevent="invite(null, query.trim())">
        <input v-model="query" type="search" placeholder="Name#TAG" />
        <button type="submit" class="lcu" :disabled="busy || !query.trim()">Invite</button>
      </form>
      <button
        v-for="friend in visibleFriends()"
        :key="friend.summonerId"
        type="button"
        class="friend"
        :disabled="busy"
        @click="invite(friend.summonerId, null)"
      >
        <img class="crest" :src="profileIcon(friend.icon)" alt="" />
        <span class="who">
          <strong>{{ friend.name }}</strong>
          <span class="status" :class="friend.status.toLowerCase()">{{ friend.status }}</span>
        </span>
      </button>
    </div>
  </section>
</template>

<style scoped>
.party {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 0.85rem 0.85rem 1rem;
}

h2 {
  margin: 0.15rem 0 0.15rem;
  color: var(--ink);
  font-family: var(--display);
  font-size: 1.35rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.lead, .count, .empty, .invite {
  margin: 0 0 0.7rem;
  color: var(--muted);
  font-size: 0.95rem;
}

.count, .invite {
  color: var(--gold);
  font-size: 0.75rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.lobby-error, .lobby-warn {
  margin: 0 0 0.45rem;
  padding: 0.55rem 0.65rem;
  font-size: 0.92rem;
  line-height: 1.35;
  white-space: pre-line;
}

.lobby-error {
  color: #ffd0d0;
  background: rgba(92, 26, 34, 0.72);
  border: 1px solid #ee241d;
}

.lobby-warn {
  color: var(--gold-soft);
  background: rgba(70, 55, 20, 0.72);
  border: 1px solid #785a28;
}

.swift { margin: 0.2rem 0 0.8rem; }

.swift-card {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  margin-bottom: 0.45rem;
}

.swift-card h3 {
  width: 3.2rem;
  margin: 0;
  color: var(--gold);
  font-family: var(--display);
  font-size: 0.68rem;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.swift-champ, .swift-role, .swift-spell {
  border: 1px solid #463714;
  background: rgba(1, 10, 19, 0.72);
  color: var(--ink);
  cursor: pointer;
}

.swift-champ {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  min-width: 0;
  flex: 1;
  padding: 0.2rem 0.4rem 0.2rem 0.2rem;
  text-align: left;
}

.swift-champ img, .swift-spell img {
  width: 2rem;
  height: 2rem;
  border-radius: 50%;
  object-fit: cover;
}

.swift-champ span {
  overflow: hidden;
  font-size: 0.85rem;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.swift-role, .swift-spell {
  height: 2.4rem;
  padding: 0 0.4rem;
  color: var(--gold-soft);
  font-family: var(--display);
  font-size: 0.68rem;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.swift-spell {
  width: 2.4rem;
  padding: 0;
  display: grid;
  place-items: center;
}

.champ-search {
  width: 100%;
  margin: 0.2rem 0 0.6rem;
  padding: 0.5rem 0.55rem;
  border: 2px solid #695625;
  background: var(--panel);
  color: var(--ink);
}

.champ-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(4.4rem, 1fr));
  gap: 0.45rem;
}

.portrait {
  border: 0;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.portrait img {
  width: 100%;
  aspect-ratio: 1;
  border-radius: 50%;
  object-fit: cover;
}

.portrait span {
  display: block;
  overflow: hidden;
  font-size: 0.62rem;
  text-align: center;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.member, .friend {
  display: flex;
  align-items: center;
  gap: 0.7rem;
  width: 100%;
  margin: 0;
  padding: 0.7rem 0.15rem;
  border: 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.22);
  background: transparent;
  text-align: left;
  cursor: pointer;
}

.member { cursor: default; }

.crest {
  width: 3.1rem;
  height: 3.1rem;
  flex: 0 0 auto;
  border-radius: 50%;
  object-fit: cover;
  border: 2px solid #ae8939;
  background: var(--panel);
}

.who { min-width: 0; flex: 1; }
.who strong {
  display: block;
  overflow: hidden;
  color: var(--ink);
  font-weight: 400;
  font-size: 1rem;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.who span, .status {
  color: var(--gold);
  font-size: 0.75rem;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.status.online { color: var(--teal); }
.status.busy { color: #e84057; }
.status.offline, .status.away { color: var(--dim); }

.role-picks { display: flex; gap: 0.35rem; }
.role-picks button, .kick {
  min-width: 4.4rem;
  padding: 0.45rem 0.4rem;
  border: 1px solid var(--gold);
  background: rgba(1, 10, 19, 0.65);
  color: var(--gold-soft);
  font-family: var(--display);
  font-size: 0.68rem;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  cursor: pointer;
}

.kick { color: #e8b0b8; border-color: var(--red); }

.invite-prompt {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  margin: 0.35rem 0 0.8rem;
  padding: 0.85rem;
  border: 0;
  background: transparent;
  color: rgba(246, 236, 216, 0.72);
  font-family: var(--display);
  font-size: 1rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  cursor: pointer;
}

.queue {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  width: 100%;
  margin: 0 0 0.15rem;
  padding: 0.85rem 0.35rem;
  border: 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.16);
  background: transparent;
  color: var(--gold-soft);
  font-family: var(--display);
  font-size: 1.05rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  text-align: left;
  cursor: pointer;
}

.queue.on { color: var(--ink); }
.queue.on .diamond { background: var(--gold); }

.diamond {
  width: 0.7rem;
  height: 0.7rem;
  flex: 0 0 auto;
  border: 2px solid var(--gold);
  background: transparent;
  transform: rotate(45deg);
}

.actions, .search {
  display: flex;
  gap: 0.45rem;
  margin-top: 0.8rem;
}

.actions .lcu, .search .lcu { flex: 1; height: 3rem; font-size: 0.92rem; }

.role-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.55rem;
  padding-top: 0.4rem;
}

.role-tile {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.55rem;
  min-height: 4.4rem;
  border: 2px solid #463714;
  background: rgba(1, 10, 19, 0.72);
  color: var(--gold-soft);
  font-family: var(--display);
  letter-spacing: 0.06em;
  text-transform: uppercase;
  cursor: pointer;
}

.role-tile:active:not(:disabled) { border-color: var(--gold); color: var(--ink); }

button:disabled { opacity: 0.45; cursor: default; }

.error {
  margin: 0.6rem 0 0;
  color: var(--red);
  font-size: 0.85rem;
}

.sheet {
  position: fixed;
  inset: 0;
  z-index: 6;
  display: flex;
  flex-direction: column;
  overflow: auto;
  padding: 0 0.9rem calc(1rem + env(safe-area-inset-bottom));
  background: #010a13 url("/magic-background.jpg") center / cover no-repeat;
}

.sheet header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 0 -0.9rem 0.6rem;
  padding: calc(0.75rem + env(safe-area-inset-top)) 0.9rem 0.75rem;
  background: rgba(0, 0, 0, 0.72);
  border-bottom: 1px solid rgba(240, 230, 210, 0.45);
}

.text {
  border: 0;
  background: transparent;
  color: var(--gold);
  font-family: var(--display);
  letter-spacing: 0.06em;
  text-transform: uppercase;
  cursor: pointer;
}

.search { align-items: stretch; }
.search input {
  flex: 1;
  min-width: 0;
  padding: 0.55rem 0.6rem;
  border: 2px solid #695625;
  background: var(--panel);
  color: var(--ink);
  font-size: 0.95rem;
}
</style>
