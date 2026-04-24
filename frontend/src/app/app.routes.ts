import { Routes } from '@angular/router';
import ChatComponent from './chat.component';

const routes: Routes = [
    { path: '', component: ChatComponent },
    { path: 'session/:id', component: ChatComponent },
    { path: '**', redirectTo: '' }
];

export default routes;
